param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [Parameter(Mandatory = $true)][string]$SharedKey,
    [Parameter(Mandatory = $true)][int]$ListenPort,
    [Parameter(Mandatory = $true)][string]$RemoteHost,
    [Parameter(Mandatory = $true)][int]$RemotePort
)

$ErrorActionPreference = "Stop"

dotnet run --project src/SideBySideSync/SideBySideSync.csproj -- `
  --folder $Folder `
  --sharedKey $SharedKey `
  --listenPort $ListenPort `
  --remoteHost $RemoteHost `
  --remotePort $RemotePort
