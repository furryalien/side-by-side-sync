# SideBySideSync

SideBySideSync is a .NET 8 peer-to-peer console prototype for synchronized folder workflows.  
This initial implementation focuses on authenticated peer connectivity and a runnable developer workflow.

## Current Scope

- .NET 8 console app entry point
- Dual-role peer behavior in a single process:
  - TCP listener (accepts inbound peers)
  - TCP connector (dials outbound peer with retry loop)
- Simple shared-key authentication handshake:
  - Listener sends random nonce
  - Connector responds with HMAC-SHA256(sharedKey, nonce)
  - Listener verifies and returns `OK`
- PowerShell helper scripts for build and run

## Project Structure

- `src/SideBySideSync/SideBySideSync.csproj` — project definition
- `src/SideBySideSync/Program.cs` — app logic (listener, connector, auth handshake)
- `
