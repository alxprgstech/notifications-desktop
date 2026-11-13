param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Iscc
)
$ErrorActionPreference = 'Stop'
if ($Version -cnotmatch '\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z' -or
    ($Version.Split('.') | Where-Object { [int]$_ -gt 65534 })) {
    throw "Invalid Windows release version: $Version"
}
$repo = Split-Path $PSScriptRoot
$artifacts = Join-Path $repo 'artifacts'
$publish = Join-Path $artifacts "publish-$([guid]::NewGuid())"
dotnet publish (Join-Path $repo 'Notify.Desktop.csproj') -c Release -r win-x64 --self-contained true -p:Version=$Version -p:DebugType=None -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

& $Iscc /Qp "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$artifacts" (Join-Path $PSScriptRoot 'Notify.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }
