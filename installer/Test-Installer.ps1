param(
    [Parameter(Mandatory)] [string] $Baseline,
    [Parameter(Mandatory)] [string] $Installer,
    [Parameter(Mandatory)] [string] $Version
)
$ErrorActionPreference = 'Stop'

function Invoke-Setup([string] $Path, [string] $Arguments, [string] $Label) {
    $log = Join-Path $env:RUNNER_TEMP "notify-$Label.log"
    $process = Start-Process -FilePath $Path -ArgumentList "$Arguments /LOG=`"$log`"" -WindowStyle Hidden -PassThru
    # The update intentionally restarts the tray app, which outlives the installer.
    if (-not $process.WaitForExit(120000)) {
        Get-Content -LiteralPath $log -Tail 80 -ErrorAction SilentlyContinue
        throw "$Label installer process timed out"
    }
    if ($process.ExitCode -ne 0) {
        Get-Content -LiteralPath $log -Tail 80
        throw "$Label failed ($($process.ExitCode))"
    }
}

function Assert-InstalledVersion([string] $Expected) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Desktop executable is missing' }
    if ((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne "$Expected.0") {
        throw "Unexpected desktop file version"
    }
    if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) { throw 'Start menu shortcut is missing' }
    $entry = Get-ItemProperty -LiteralPath $uninstallKey
    if ($entry.DisplayVersion -ne $Expected -or $entry.DisplayName -ne 'ALXPRGS Notify Desktop') {
        throw "Unexpected installed-apps registration: $($entry.DisplayName), version $($entry.DisplayVersion)"
    }
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'Uninstaller is missing' }
    if (Get-ChildItem -LiteralPath $directory -Recurse -File -Include *.pdb,*.cs,*.csproj,*.dpapi,*Specs* ) {
        throw 'Unexpected development files or credentials in the installation'
    }
}

function Start-Tray {
    $process = Start-Process -FilePath $exe -ArgumentList '--background' -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Desktop failed to start ($($process.ExitCode))" }
        $ready = [Threading.EventWaitHandle]::TryOpenExisting('Local\ALXPRGS.Notify.Desktop.Shutdown', [ref]$script:signal)
        if ($ready) {
            $script:signal.Dispose()
            $script:signal = $null
            return $process
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Desktop never became ready'
}

$baselinePath = (Resolve-Path -LiteralPath $Baseline).Path
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$directory = Join-Path $env:RUNNER_TEMP 'Notify Smoke'
$exe = Join-Path $directory 'ALXPRGS.Notify.Desktop.exe'
$uninstaller = Join-Path $directory 'unins000.exe'
$data = Join-Path $env:LOCALAPPDATA 'ALXPRGS\Notify Desktop'
$settings = Join-Path $data 'settings.json'
$credential = Join-Path $data 'source.dpapi'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'ALXPRGS Notify Desktop.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{02AA4972-2C68-4135-879D-4F6257803F9D}_is1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName = 'ALXPRGS Notify Desktop'
$runCommand = "`"$exe`" --background"
$script:signal = $null
if ([Threading.Mutex]::TryOpenExisting('Local\ALXPRGS.Notify.Desktop', [ref]$script:signal)) {
    $script:signal.Dispose()
            $script:signal = $null
    throw 'Smoke test requires a profile with no running Notify Desktop instance'
}
if ((Test-Path $data) -or (Test-Path $directory) -or (Test-Path $shortcut) -or (Test-Path $uninstallKey)) {
    throw 'Smoke test requires a fresh profile and install directory'
}
if (Get-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue) {
    throw 'Smoke test requires a fresh Run entry'
}
$flags = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
Invoke-Setup $baselinePath "$flags /DIR=`"$directory`"" 'baseline'
Assert-InstalledVersion '0.0.0'

New-Item -ItemType Directory -Force -Path $data | Out-Null
# A token for a different origin exercises DPAPI without starting a live bridge.
'{"origin":"https://example.invalid","client":"Codex","chat":{"id":"smoke-chat","name":"Installer smoke test"},"theme":"Dark","keepInTray":true}' |
    Set-Content -LiteralPath $settings -Encoding utf8
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$plain = [Text.Encoding]::UTF8.GetBytes('{"origin":"https://another.invalid","token":"synthetic-test-token"}')
$scope = [Security.Cryptography.DataProtectionScope]::CurrentUser
$protected = [Security.Cryptography.ProtectedData]::Protect($plain, $null, $scope)
[IO.File]::WriteAllBytes($credential, $protected)
$settingsHash = (Get-FileHash -LiteralPath $settings).Hash
$credentialHash = (Get-FileHash -LiteralPath $credential).Hash
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -LiteralPath $runKey -Name $runName -Value $runCommand

$tray = Start-Tray
Invoke-Setup $installerPath "$flags /NOTIFYUPDATE" 'upgrade'
if (-not $tray.WaitForExit(5000) -or $tray.ExitCode -ne 0) { throw 'Upgrade did not close the tray process cleanly' }
Assert-InstalledVersion $Version
if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash) { throw 'Update changed preferences or chat binding' }
if ((Get-FileHash -LiteralPath $credential).Hash -ne $credentialHash) { throw 'Update changed the DPAPI file' }
$decoded = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($credential), $null, $scope)
if ([Text.Encoding]::UTF8.GetString($decoded) -ne [Text.Encoding]::UTF8.GetString($plain)) { throw 'DPAPI token is unreadable' }
if ((Get-ItemPropertyValue -LiteralPath $runKey -Name $runName) -ne $runCommand) { throw 'Update changed autostart' }

$restartDeadline = [DateTime]::UtcNow.AddSeconds(20)
do {
    $restarted = Get-Process -Name 'ALXPRGS.Notify.Desktop' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe } | Select-Object -First 1
    if ($restarted -and [Threading.EventWaitHandle]::TryOpenExisting('Local\ALXPRGS.Notify.Desktop.Shutdown', [ref]$script:signal)) {
        $script:signal.Dispose()
        $script:signal = $null
        break
    }
    $restarted = $null
    Start-Sleep -Milliseconds 200
} while ([DateTime]::UtcNow -lt $restartDeadline)
if (-not $restarted) { throw 'The update did not restart Notify Desktop' }
$tray = $restarted
# Open the process handle before uninstall so ExitCode remains available after it exits.
$null = $tray.Handle
Invoke-Setup $uninstaller $flags 'uninstall'
if (-not $tray.WaitForExit(20000) -or $tray.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $env:RUNNER_TEMP 'notify-uninstall.log') -Tail 80 -ErrorAction SilentlyContinue
    throw 'Uninstall did not close the tray process cleanly'
}
# Inno's uninstaller can remove its own executable after the launcher exits.
$cleanupDeadline = [DateTime]::UtcNow.AddSeconds(20)
do {
    $remaining = @($exe, $shortcut, $uninstallKey, $uninstaller) | Where-Object { Test-Path -LiteralPath $_ }
    if (-not $remaining) { break }
    Start-Sleep -Milliseconds 200
} while ([DateTime]::UtcNow -lt $cleanupDeadline)
if ($remaining) {
    Get-Content -LiteralPath (Join-Path $env:RUNNER_TEMP 'notify-uninstall.log') -Tail 80 -ErrorAction SilentlyContinue
    throw "Uninstall left these paths: $($remaining -join ', ')"
}
if (Get-ItemProperty -LiteralPath $runKey -Name $runName -ErrorAction SilentlyContinue) {
    throw 'Uninstall left its autostart entry'
}
if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash -or
    (Get-FileHash -LiteralPath $credential).Hash -ne $credentialHash) {
    throw 'Uninstall changed user data'
}
Write-Host 'Clean installation, running upgrade, DPAPI preservation and running uninstall passed.'
