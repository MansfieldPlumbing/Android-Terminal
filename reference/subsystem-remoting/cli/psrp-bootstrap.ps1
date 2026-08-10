# psrp-bootstrap.ps1 — hydrates a fresh PSRP server session (Rs, \Capability\Remoting\Psrp).
#
# A session accepted on the Subsystem.Psrp named pipe is created by the engine's default OutOfProc
# server path with the MINIMAL core session state. On Android the built-in module manifests are not
# on disk (assemblies are embedded in the APK), so module autoloading cannot resolve Management/
# Utility — the assemblies are already loaded in-process, they just need to be imported by reference.
# Importing the Subsystem assembly gives the remote session the same compiled cmdlet surface
# (Get-VomOwner, Get-Capability, …) as the main runspace: a PSRP session IS a Subsystem session.
#
# This is DATA, not C# (SS001): garden the default surface of remote sessions by editing this file.
foreach ($assemblyName in @(
        'Microsoft.PowerShell.Commands.Management',
        'Microsoft.PowerShell.Commands.Utility',
        'Subsystem')) {
    try {
        Import-Module -Assembly ([System.Reflection.Assembly]::Load($assemblyName)) -ErrorAction SilentlyContinue
    } catch {
        # Degrade per-assembly: a session without one import is still a session.
    }
}
$env:POWERSHELL_TELEMETRY_OPTOUT = '1'

# Session-surface helpers — verbs the presenters call STRUCTURED (no script splicing at the seam).

# THE BYTE LANE (preferred): stage a file as a Float32 VOM region (a first-class, refcounted,
# Task-Manager-visible kernel handle under \Capability\IPC\TextureBridge\*) and hand the presenter the
# handle. The WebView pulls the bytes over the CoreCLR->WebView interop (vom://<handle>, or its
# loopback alias /vom/<handle>) — binary end to end, no JSON envelope. Bytes pad into the Float32
# layout (VOM-SPEC: Format is a media-agnostic layout enum); Size carries the true byte length so
# the consumer slices the padding off. Close-SsFileLane releases the region (free-at-zero).
function Publish-SsFileLane {
    param([Parameter(Mandatory)][string]$LiteralPath)
    $bytes  = [System.IO.File]::ReadAllBytes($LiteralPath)
    $floats = [float[]]::new([int][Math]::Ceiling($bytes.Length / 4.0))
    if ($bytes.Length -gt 0) { [System.Buffer]::BlockCopy($bytes, 0, $floats, 0, $bytes.Length) }
    $handle = 'file-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
    [Subsystem.VomInterop]::SetTexture($handle, $floats)
    [pscustomobject]@{ Handle = $handle; Size = $bytes.Length }
}

function Close-SsFileLane {
    param([Parameter(Mandatory)][string]$Handle)
    [Subsystem.VomInterop]::Release($Handle)
}

# Base64 fallback for environments where the vom:// lane can't be fetched (kept grounded, not
# primary — the lane is the preferred transport).
function ConvertTo-SsBase64 {
    param([Parameter(Mandatory)][string]$LiteralPath)
    [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($LiteralPath))
}

