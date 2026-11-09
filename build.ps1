# Builds both widgets with the C# compiler that ships with Windows (.NET Framework 4.8).
# No SDK, NuGet or runtime install needed. Output: .\bin\BatteryWidget.exe and .\bin\VolumeMediaWidget.exe
param([switch]$Run, [ValidateSet('', 'battery', 'volume')][string]$Only = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wm   = "$env:WINDIR\System32\WinMetadata"
$gac  = "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL"
$csc  = "$fw\csc.exe"
$bin  = Join-Path $root 'bin'
$obj  = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $bin, $obj | Out-Null

function Facade($name, $token = 'b03f5f7f11d50a3a') { "$gac\$name\v4.0_4.0.0.0__$token\$name.dll" }

$refs = @(
  'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
  "$wm\Windows.Foundation.winmd", "$wm\Windows.Devices.winmd", "$wm\Windows.Media.winmd", "$wm\Windows.Storage.winmd", "$wm\Windows.Networking.winmd",
  (Facade 'System.Runtime'), (Facade 'System.Threading.Tasks'), (Facade 'System.Runtime.InteropServices.WindowsRuntime')
) | ForEach-Object { "/r:$_" }

$common = @('/nologo', '/optimize+', '/platform:x64', '/target:winexe', '/debug-', "/win32manifest:$root\src\Shared\app.manifest")
$shared = Get-ChildItem "$root\src\Shared\*.cs" | Where-Object Name -ne 'IconGen.cs' | ForEach-Object FullName

function Compile($out, $extra) {
  Write-Host "Building $out" -ForegroundColor Cyan
  & $csc @common @refs "/out:$bin\$out" @extra
  if ($LASTEXITCODE -ne 0) { throw "Build failed: $out" }
}

# 1. icons
& $csc /nologo /target:exe "/out:$obj\IconGen.exe" /r:System.Drawing.dll "$root\src\Shared\IconGen.cs" "$root\src\Shared\Gfx.cs"
if ($LASTEXITCODE -ne 0) { throw 'IconGen build failed' }
& "$obj\IconGen.exe" $obj

# 2. apps
foreach ($p in 'BatteryWidget', 'VolumeMediaWidget') { Get-Process $p -ErrorAction SilentlyContinue | Stop-Process -Force }
Start-Sleep -Milliseconds 300
if ($Only -ne 'volume') { Compile 'BatteryWidget.exe'     (@("/win32icon:$obj\battery.ico") + $shared + (Get-ChildItem "$root\src\Battery\*.cs" | ForEach-Object FullName)) }
if ($Only -ne 'battery') { Compile 'VolumeMediaWidget.exe' (@("/win32icon:$obj\media.ico")   + $shared + (Get-ChildItem "$root\src\Volume\*.cs"  | ForEach-Object FullName)) }

Write-Host "Done -> $bin" -ForegroundColor Green
if ($Run) {
  if ($Only -ne 'volume') { Start-Process "$bin\BatteryWidget.exe" }
  if ($Only -ne 'battery') { Start-Process "$bin\VolumeMediaWidget.exe" }
}
