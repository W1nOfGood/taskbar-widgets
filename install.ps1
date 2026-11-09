# Taskbar Widgets: install from PowerShell in one line (current user only, no admin needed).
#
#   irm https://raw.githubusercontent.com/W1nOfGood/taskbar-widgets/main/install.ps1 | iex
#
# Downloads the latest TaskbarWidgetsSetup.exe from GitHub Releases, checks its SHA-256 against the
# digest GitHub publishes for that file, and installs it silently. Running it again updates or repairs.
#
# Options need the scriptblock form:
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/W1nOfGood/taskbar-widgets/main/install.ps1))) -Uninstall
#
#   -Uninstall        remove Taskbar Widgets (your settings are kept)
#   -DeleteSettings   with -Uninstall: also delete the settings in %APPDATA%\TaskbarWidgets
#   -Window           open the normal setup window instead of installing silently
#
# Plain ASCII on purpose: Windows PowerShell 5.1 misreads UTF-8 without a BOM.
param([switch]$Uninstall, [switch]$DeleteSettings, [switch]$Window)

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue' # the progress bar makes downloads very slow in Windows PowerShell 5.1
    $repo = 'W1nOfGood/taskbar-widgets'
    $assetName = 'TaskbarWidgetsSetup.exe'
    $regKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskbarWidgets'

    function Installed { Get-ItemProperty $regKey -ErrorAction SilentlyContinue }

    function Run($file, $arguments) {
        # Process.WaitForExit waits for the setup only, not for the widgets it starts
        $p = [Diagnostics.Process]::Start($file, $arguments)
        $p.WaitForExit()
        return $p.ExitCode
    }

    if ($Uninstall) {
        $info = Installed
        if (-not $info -or -not $info.InstallLocation) { Write-Host 'Taskbar Widgets is not installed. Nothing to do.'; return }
        $uninstaller = Join-Path $info.InstallLocation 'Uninstall.exe'
        if (-not (Test-Path $uninstaller)) { throw "Taskbar Widgets: $uninstaller is missing. Remove it from Settings > Apps > Installed apps instead." }
        Write-Host 'Removing Taskbar Widgets...'
        $a = '--uninstall --quiet'
        if ($DeleteSettings) { $a += ' --delete-settings' }
        $code = Run $uninstaller $a
        if ($code -ne 0 -or (Installed)) { throw "Taskbar Widgets: the uninstaller failed (exit code $code)." }
        Write-Host 'Taskbar Widgets was removed.' -ForegroundColor Green
        if (-not $DeleteSettings) { Write-Host 'Your settings are still in %APPDATA%\TaskbarWidgets (delete that folder to remove them too).' }
        return
    }

    if ([Environment]::OSVersion.Version.Major -lt 10 -or -not [Environment]::Is64BitOperatingSystem) {
        throw 'Taskbar Widgets needs 64-bit Windows 10 or 11.'
    }
    # Windows PowerShell 5.1 may still default to TLS 1.0, which GitHub refuses
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $url = "https://github.com/$repo/releases/latest/download/$assetName"
    $digest = $null
    $version = 'latest'
    try {
        $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'taskbar-widgets-install-script' } -UseBasicParsing
        $asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
        if (-not $asset) { throw "the latest release has no $assetName" }
        $url = $asset.browser_download_url
        $digest = $asset.digest
        $version = $release.tag_name
    }
    catch { Write-Warning "Couldn't read the release info from GitHub ($($_.Exception.Message)). Downloading the latest setup without a checksum." }

    $old = Installed
    if ($old -and $old.DisplayVersion) { Write-Host "Taskbar Widgets $($old.DisplayVersion) is installed. Updating to $version..." }
    else { Write-Host "Installing Taskbar Widgets $version..." }

    # a fresh, empty folder of our own, deleted afterwards
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('TaskbarWidgetsSetup-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        $setup = Join-Path $tmp $assetName
        Invoke-WebRequest $url -OutFile $setup -UseBasicParsing
        if ($digest -match '^sha256:([0-9a-fA-F]{64})$') {
            $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
            if ($hash -ne $Matches[1].ToUpperInvariant()) { throw "Taskbar Widgets: the download is damaged (SHA-256 mismatch). Nothing was installed. Try again." }
            Write-Host 'Download verified (SHA-256 matches the release).'
        }
        elseif ($digest) { Write-Warning "Unknown checksum format '$digest', skipping the check." }

        if ($Window) {
            Write-Host 'The setup window is open. Finish there.'
            $code = Run $setup ''
            if ($code -ne 0) { throw "Taskbar Widgets: setup exited with code $code." }
            return
        }
        $code = Run $setup '--quiet'
        if ($code -ne 0) { throw "Taskbar Widgets: setup failed (exit code $code). Nothing else was changed." }
    }
    finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

    $info = Installed
    if (-not $info) { throw 'Taskbar Widgets: setup finished, but the app is not registered as installed.' }
    Start-Sleep -Seconds 2
    $running = @(Get-Process -Name BatteryWidget, VolumeMediaWidget -ErrorAction SilentlyContinue).Count

    Write-Host ''
    Write-Host "Taskbar Widgets $($info.DisplayVersion) is installed." -ForegroundColor Green
    Write-Host "  Folder:     $($info.InstallLocation)"
    Write-Host "  Running:    $running of 2 widgets (look at your taskbar)"
    Write-Host '  Startup:    starts with Windows, updates itself'
    Write-Host '  Settings:   click a widget; right-click it for the menu'
    Write-Host '  Uninstall:  Settings > Apps > Installed apps > Taskbar Widgets'
}
