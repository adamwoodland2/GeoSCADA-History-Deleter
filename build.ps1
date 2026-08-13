# Builds the Geo SCADA History Deleter.
#
# Uses the C# compiler that ships with the .NET Framework, so nothing needs to be installed to
# build this beyond Windows itself plus the Geo SCADA client (for the ClearScada.Client reference).
# Produces a 64-bit and a 32-bit exe; use the one matching the installed Geo SCADA client.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src'
$bin = Join-Path $root 'bin'

function Find-ClearScadaReference {
    $key = 'HKLM:\SOFTWARE\Schneider Electric\ClearSCADA'
    $candidates = @()
    if (Test-Path $key) {
        $props = Get-ItemProperty $key
        $candidates += $props.InstallLocation
        $candidates += $props.InstallLocationx86
    }
    $candidates += 'C:\Program Files\Schneider Electric\ClearSCADA'
    $candidates += 'C:\Program Files (x86)\Schneider Electric\ClearSCADA'

    foreach ($dir in $candidates) {
        if ([string]::IsNullOrWhiteSpace($dir)) { continue }
        $dll = Join-Path $dir.TrimEnd('\') 'ClearScada.Client.dll'
        if (Test-Path $dll) { return $dll }
    }
    throw "ClearScada.Client.dll not found. Install the Geo SCADA client, or edit this script to point at it."
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw "The .NET Framework 4.x C# compiler was not found." }

$clearScada = Find-ClearScadaReference
Write-Host "Compiler  : $csc"
Write-Host "Reference : $clearScada"

New-Item -ItemType Directory -Force $bin | Out-Null
$sources = Get-ChildItem $src -Filter '*.cs' | ForEach-Object { $_.FullName }
$manifest = Join-Path $src 'app.manifest'

$references = @(
    "/r:$clearScada"
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
    '/r:System.Xml.dll'
)

$targets = @(
    @{ Platform = 'x64'; Output = 'HistoryDeleter.exe' }
    @{ Platform = 'x86'; Output = 'HistoryDeleter32.exe' }
)

foreach ($target in $targets) {
    $out = Join-Path $bin $target.Output
    Write-Host "Building $($target.Output) ($($target.Platform))..."

    $arguments = @(
        '/nologo'
        '/target:winexe'
        "/platform:$($target.Platform)"
        "/out:$out"
        "/win32manifest:$manifest"
        '/optimize+'
        '/warnaserror-'
        # ServerNode.Connect(string) is flagged obsolete but is the overload the client API itself uses.
        '/nowarn:0618'
    ) + $references + $sources

    & $csc $arguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $($target.Platform)." }
}

Write-Host ""
Write-Host "Built:"
Get-ChildItem $bin -Filter 'HistoryDeleter*.exe' | ForEach-Object {
    Write-Host ("  {0,-24} {1,8:N0} bytes" -f $_.Name, $_.Length)
}
