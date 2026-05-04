using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

if (args.Length == 0)
{
    PrintHelp();
    return;
}

var options = ParseArgs(args);

if (!options.TryGetValue("folder", out var folder) ||
    !options.TryGetValue("sharedkey", out var sharedKey) ||
    !options.TryGetValue("listenport", out var listenPortStr) ||
    !options.TryGetValue("remotehost", out var remoteHost) ||
    !options.TryGetValue("remoteport", out var remotePortStr))
{
    Console.WriteLine("Missing required arguments.");
    PrintHelp();
    return;
}

if (!int.TryParse(listenPortStr, out var listenPort) ||
    !int.TryParse(remotePortStr, out var remotePort))
{
    Console.WriteLine("listenPort and remotePort must be integers.");
    return;
}

Directory.CreateDirectory(folder);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine("SideBySideSync peer starting...");
Console.WriteLine($"Folder: {folder}");
Console.WriteLine($"ListenPort: {listenPort}");
Console.WriteLine($"Remote: {remoteHost}:{remotePort}");

var syncState = new SyncState(folder);

var listenerTask = RunListenerAsync(listenPort, sharedKey, syncState, cts.Token);
var connectorTask = RunConnectorAsync(remoteHost, remotePort, sharedKey, syncState, cts.Token);
var watcherTask = RunFileWatcherAsync(folder, syncState, cts.Token);

await Task.WhenAll(listenerTask, connectorTask, watcherTask);

// ===== File Watcher =====
static async Task RunFileWatcherAsync(string folder, SyncState state, CancellationToken token)
{
    await Task.Delay(1000, token); // Give time for initial connection

    using var watcher = new FileSystemWatcher(folder)
    {
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
        IncludeSubdirectories = true,
        EnableRaisingEvents = true
    };

    var changeQueue = new ConcurrentQueue<string>();
    var changeEvent = new SemaphoreSlim(0);

    void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType == WatcherChangeTypes.Changed || e.ChangeType == WatcherChangeTypes.Created)
        {
            changeQueue.Enqueue(e.FullPath);
            changeEvent.Release();
        }
    }

    watcher.Created += OnChanged;
    watcher.Changed += OnChanged;

    Console.WriteLine("[Watcher] Monitoring folder for changes...");

    try
    {
        while (!token.IsCancellationRequested)
        {
            await changeEvent.WaitAsync(token);
            
            // Debounce: wait a bit to collect multiple rapid changes
            await Task.Delay(500, token);

            var processedFiles = new HashSet<string>();
            while (changeQueue.TryDequeue(out var filePath))
            {
                if (!processedFiles.Add(filePath))
                    continue;

                // Check if we should ignore this write (we just created/updated it from remote)
                var relativePath = Path.GetRelativePath(state.Folder, filePath);
                if (state.IgnoreWrites.TryRemove(relativePath, out var ignoreUntil))
                {
                    if (DateTime.UtcNow < ignoreUntil)
                    {
                        Console.WriteLine($"[Watcher] Ignoring self-write: {relativePath}");
                        continue;
                    }
                }

                if (File.Exists(filePath))
                {
                    await SendFileAsync(state, filePath, token);
                }
            }
        }
    }
    catch (OperationCanceledException) { }
}

// ===== Listener =====
static async Task RunListenerAsync(int port, string sharedKey, SyncState state, CancellationToken token)
{
    var listener = new TcpListener(IPAddress.Any, port);
    listener.Start();
    Console.WriteLine($"[Listener] Listening on 0.0.0.0:{port}");

    try
    {
        while (!token.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(token);
            _ = Task.Run(() => HandleClientAsync(client, sharedKey, state, token), token);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        listener.Stop();
    }
}

static async Task HandleClientAsync(TcpClient client, string sharedKey, SyncState state, CancellationToken token)
{
    using (client)
    {
        var stream = client.GetStream();

        // Authentication
        var nonce = RandomNumberGenerator.GetBytes(16);
        await stream.WriteAsync(nonce, token);

        var response = new byte[32];
        var read = 0;
        while (read < response.Length)
        {
            var n = await stream.ReadAsync(response.AsMemory(read, response.Length - read), token);
            if (n == 0) return;
            read += n;
        }

        var expected = ComputeHmac(sharedKey, nonce);
        if (!CryptographicOperations.FixedTimeEquals(response, expected))
        {
            Console.WriteLine("[Listener] Auth failed.");
            return;
        }

        await stream.WriteAsync(Encoding.UTF8.GetBytes("OK\n"), token);
        Console.WriteLine("[Listener] Peer authenticated. Connection established.");

        // Set as active peer stream
        await state.StreamLock.WaitAsync(token);
        try
        {
            state.PeerStream?.Close();
            state.PeerStream = stream;
        }
        finally
        {
            state.StreamLock.Release();
        }

        // Receive files from peer
        await ReceiveFilesAsync(state, stream, token);
    }
}

// ===== Connector =====
static async Task RunConnectorAsync(string host, int port, string sharedKey, SyncState state, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(host, port, token);
            Console.WriteLine($"[Connector] Connected to {host}:{port}");

            var stream = client.GetStream();

            // Authentication
            var nonce = new byte[16];
            var read = 0;
            while (read < nonce.Length)
            {
                var n = await stream.ReadAsync(nonce.AsMemory(read, nonce.Length - read), token);
                if (n == 0) throw new IOException("Disconnected during handshake.");
                read += n;
            }

            var hmac = ComputeHmac(sharedKey, nonce);
            await stream.WriteAsync(hmac, token);

            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(token);
            if (line != "OK")
            {
                Console.WriteLine("[Connector] Auth failed.");
                client.Close();
                continue;
            }

            Console.WriteLine("[Connector] Auth success. Connection established.");

            // This stream is for receiving only; sending happens via listener's peer stream
            await ReceiveFilesAsync(state, stream, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[Connector] {ex.Message}");
        }

        if (!token.IsCancellationRequested)
            await Task.Delay(TimeSpan.FromSeconds(3), token);
    }
}

// ===== File Transfer Protocol =====
static async Task SendFileAsync(SyncState state, string fullPath, CancellationToken token)
{
    await state.StreamLock.WaitAsync(token);
    try
    {
        if (state.PeerStream == null || !state.PeerStream.CanWrite)
        {
            return; // Not connected
        }

        var relativePath = Path.GetRelativePath(state.Folder, fullPath);
        
        // Read file with retry for locked files
        byte[] fileData;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                fileData = await File.ReadAllBytesAsync(fullPath, token);
                break;
            }
            catch (IOException) when (i < 2)
            {
                await Task.Delay(100, token);
                continue;
            }
        }

        fileData = await File.ReadAllBytesAsync(fullPath, token);

        // Protocol: [MessageType:4][PathLength:4][Path:variable][DataLength:8][Data:variable]
        var pathBytes = Encoding.UTF8.GetBytes(relativePath);
        
        await state.PeerStream.WriteAsync(Encoding.ASCII.GetBytes("FILE"), token);
        await state.PeerStream.WriteAsync(BitConverter.GetBytes(pathBytes.Length), token);
        await state.PeerStream.WriteAsync(pathBytes, token);
        await state.PeerStream.WriteAsync(BitConverter.GetBytes((long)fileData.Length), token);
        await state.PeerStream.WriteAsync(fileData, token);
        await state.PeerStream.FlushAsync(token);

        Console.WriteLine($"[Sync] Sent: {relativePath} ({fileData.Length} bytes)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Sync] Failed to send file: {ex.Message}");
        state.PeerStream = null;
    }
    finally
    {
        state.StreamLock.Release();
    }
}

static async Task ReceiveFilesAsync(SyncState state, NetworkStream stream, CancellationToken token)
{
    try
    {
        while (!token.IsCancellationRequested)
        {
            // Read message type
            var typeBytes = new byte[4];
            var bytesRead = await ReadExactlyAsync(stream, typeBytes, token);
            if (bytesRead == 0) break;

            var msgType = Encoding.ASCII.GetString(typeBytes);
            if (msgType != "FILE")
            {
                Console.WriteLine($"[Sync] Unknown message type: {msgType}");
                break;
            }

            // Read path length
            var pathLenBytes = new byte[4];
            await ReadExactlyAsync(stream, pathLenBytes, token);
            var pathLen = BitConverter.ToInt32(pathLenBytes);

            // Read path
            var pathBytes = new byte[pathLen];
            await ReadExactlyAsync(stream, pathBytes, token);
            var relativePath = Encoding.UTF8.GetString(pathBytes);

            // Read data length
            var dataLenBytes = new byte[8];
            await ReadExactlyAsync(stream, dataLenBytes, token);
            var dataLen = BitConverter.ToInt64(dataLenBytes);

            // Read data
            var data = new byte[dataLen];
            await ReadExactlyAsync(stream, data, token);

            // Write file
            var fullPath = Path.Combine(state.Folder, relativePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Mark this file to be ignored by watcher
            state.IgnoreWrites[relativePath] = DateTime.UtcNow.AddSeconds(2);

            await File.WriteAllBytesAsync(fullPath, data, token);
            Console.WriteLine($"[Sync] Received: {relativePath} ({data.Length} bytes)");
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Console.WriteLine($"[Sync] Receive error: {ex.Message}");
    }
}

static async Task<int> ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
{
    var totalRead = 0;
    while (totalRead < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(totalRead), token);
        if (read == 0) return 0; // Connection closed
        totalRead += read;
    }
    return totalRead;
}

// ===== Helper Functions =====
static Dictionary<string, string> ParseArgs(string[] args)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var arg in args)
    {
        var parts = arg.Split('=', 2);
        if (parts.Length == 2)
        {
            dict[parts[0].TrimStart('-')] = parts[1];
        }
    }
    return dict;
}

static byte[] ComputeHmac(string key, byte[] data)
{
    var keyBytes = Encoding.UTF8.GetBytes(key);
    using var hmac = new HMACSHA256(keyBytes);
    return hmac.ComputeHash(data);
}

static void PrintHelp()
{
    Console.WriteLine("Usage: SideBySideSync --folder=<path> --sharedkey=<key> --listenport=<port> --remotehost=<host> --remoteport=<port>");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("  SideBySideSync --folder=./sync --sharedkey=secret123 --listenport=5001 --remotehost=localhost --remoteport=5002");
}

// ===== SyncState class to manage shared state =====
class SyncState
{
    public string Folder { get; }
    public NetworkStream? PeerStream { get; set; }
    public readonly SemaphoreSlim StreamLock = new(1, 1);
    public readonly ConcurrentDictionary<string, DateTime> IgnoreWrites = new();

    public SyncState(string folder)
    {
        Folder = folder;
    }
}
