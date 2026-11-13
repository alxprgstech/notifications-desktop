$ErrorActionPreference = 'Stop'
$installer = Join-Path $env:RUNNER_TEMP 'innosetup-7.1.1-x64.exe'
$destination = Join-Path $env:RUNNER_TEMP 'InnoSetup'
Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' -OutFile $installer
$expected = '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0'
if ((Get-FileHash $installer -Algorithm SHA256).Hash -ne $expected) {
    throw 'Inno Setup download failed SHA-256 verification'
}
$setup = Start-Process -FilePath $installer -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /DIR=`"$destination`"" -WindowStyle Hidden -PassThru -Wait
if ($setup.ExitCode -ne 0) { throw "Inno Setup installation failed ($($setup.ExitCode))" }
$iscc = Join-Path $destination 'ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) { throw 'ISCC.exe is missing' }
"ISCC=$iscc" >> $env:GITHUB_ENV
