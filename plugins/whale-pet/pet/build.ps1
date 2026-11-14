# build.ps1 - compile WhalePet.cs with the system .NET Framework C# compiler.
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Clean]
#
# A running WhalePet.exe instance locks the exe file on Windows, so the build
# compiles to a temporary file and only then swaps it into place; if the target is
# locked the old image is renamed aside first (a loaded exe may be renamed) so a
# rebuild always succeeds without killing a running pet.
param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $dir 'WhalePet.cs'
$out = Join-Path $dir 'WhalePet.exe'
$tmpOut = Join-Path $dir 'WhalePet.build.exe'

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
if (-not (Test-Path $src)) { throw "source not found: $src" }

if ($Clean) {
    foreach ($p in @($out, (Join-Path $dir 'WhalePet.pdb'), $tmpOut)) {
        if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
    }
}

# Compile exactly as the DSH integration contract requires (same command line).
& $csc /nologo /target:winexe /optimize+ /platform:anycpu /out:$tmpOut /r:System.Drawing.dll $src
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $tmpOut)) { throw "WhalePet.build.exe was not produced" }

# Swap the fresh build into place.
$archived = $out + '.old'
if (Test-Path $out) {
    try {
        Move-Item -LiteralPath $out -Destination $archived -Force -ErrorAction Stop
        Remove-Item -LiteralPath $archived -Force -ErrorAction SilentlyContinue
    } catch {
        throw "cannot replace $out (locked and rename failed): $($_.Exception.Message)"
    }
}
Move-Item -LiteralPath $tmpOut -Destination $out -Force
$info = Get-Item $out
Write-Output ("BUILD OK: {0} ({1} bytes, {2:yyyy-MM-dd HH:mm:ss})" -f $info.FullName, $info.Length, $info.LastWriteTime)
