using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

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

var listenerTask = RunListenerAsync(listenPort, sharedKey, cts.Token);
var connectorTask = RunConnectorAsync(remoteHost, remotePort, sharedKey, cts.Token);

await Task.WhenAll(listenerTask, connectorTask);

static async Task RunListenerAsync(int port, string sharedKey, CancellationToken token)
{
    var listener = new TcpListener(IPAddress.Any, port);
    listener.Start();
    Console.WriteLine($"[Listener] Listening on 0.0.0.0:{port}");

    try
    {
        while (!token.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(token);
            _ = Task.Run(() => HandleClientAsync(client, sharedKey, token), token);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        listener.Stop();
    }
}

static async Task HandleClientAsync(TcpClient client, string sharedKey, CancellationToken token)
{
    using (client)
    {
        var stream = client.GetStream();

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
        Console.WriteLine("[Listener] Peer authenticated.");
    }
}

static async Task RunConnectorAsync(string host, int port, string sharedKey, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, token);
            Console.WriteLine($"[Connector] Connected to {host}:{port}");

            var stream = client.GetStream();

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
            if (line == "OK")
            {
                Console.WriteLine("[Connector] Auth success.");
            }
            else
            {
                Console.WriteLine("[Connector] Auth failed.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[Connector] {ex.Message}");
        }

        await Task.Delay(TimeSpan.FromSeconds(3), token);
    }
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var dict = new Dictionary<string, string>(String
