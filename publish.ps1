<#
.SYNOPSIS
    Build Divoom PC Monitor for Windows x64 and ARM64.

.DESCRIPTION
    Runs the tests, then publishes a self-contained folder for each runtime.
    Pass -Version to stamp that version into the exe and the zip name.
    A GitHub release does this from a version tag. The exe and the runtime
    files sit in one folder. Do not publish as a single file.

.EXAMPLE
    ./publish.ps1

.EXAMPLE
    ./publish.ps1 -Version 2.0.2

.EXAMPLE
    ./publish.ps1 -Runtime win-arm64 -Version 0.1.1 -SkipTest
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'win-x64', 'win-arm64')]
    [string] $Runtime = 'all',

    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $Version,

    [switch] $SkipTest
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = (Get-Location).Path
}

function Find-DotNet {
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) {
        return $local
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    throw 'The .NET 10 SDK was not found. Install it from https://dot.net/download and run this script again.'
}

function Get-DefaultVersion {
    $propsPath = Join-Path $Root 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw "Could not find $propsPath"
    }

    [xml] $props = Get-Content -LiteralPath $propsPath
    $value = $props.Project.PropertyGroup.Version
    if ($value -is [Array]) {
        $value = $value | Select-Object -First 1
    }

    $text = [string] $value
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "Directory.Build.props does not set Version."
    }

    return $text.Trim()
}

function ConvertTo-VersionStamp {
    param([string] $Raw)

    $text = $Raw.Trim()
    if ($text.StartsWith('v') -or $text.StartsWith('V')) {
        $text = $text.Substring(1)
    }

    if ($text -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?<label>[-+].*)?$') {
        throw "Version must look like 0.1.0 or v0.1.0. Got '$Raw'."
    }

    $numeric = '{0}.{1}.{2}' -f $Matches.major, $Matches.minor, $Matches.patch
    $informational = $numeric
    if (-not [string]::IsNullOrEmpty($Matches.label)) {
        $informational = $numeric + $Matches.label
    }

    return [pscustomobject]@{
        Numeric        = $numeric
        Assembly       = "$numeric.0"
        Informational  = $informational
        FileName       = ($informational -replace '[\\/:*?"<>|]', '-')
    }
}

$dotnet = Find-DotNet
$dotnetRoot = Split-Path -Parent $dotnet
$env:DOTNET_ROOT = $dotnetRoot
$env:Path = $dotnetRoot + [IO.Path]::PathSeparator + $env:Path

$appProject = Join-Path $Root 'src\DivoomPcMonitor.App\DivoomPcMonitor.App.csproj'
$solution = Join-Path $Root 'DivoomPcMonitor.sln'
if (-not (Test-Path -LiteralPath $appProject)) {
    throw "Could not find $appProject"
}

if (-not (Test-Path -LiteralPath $solution)) {
    throw "Could not find $solution"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-DefaultVersion
}

$stamp = ConvertTo-VersionStamp $Version
Write-Host "Using $dotnet"
Write-Host "Version $($stamp.Informational)"
& $dotnet --version

if (-not $SkipTest) {
    Write-Host 'Building and testing...'
    & $dotnet test $solution -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$runtimes = @('win-x64', 'win-arm64')
if ($Runtime -ne 'all') {
    $runtimes = @($Runtime)
}

foreach ($rid in $runtimes) {
    $output = Join-Path $Root ("dist\DivoomPcMonitor-{0}-{1}" -f $stamp.FileName, $rid)
    $zip = "$output.zip"
    $hashFile = "$zip.sha256"
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }

    foreach ($extra in @($zip, $hashFile)) {
        if (Test-Path -LiteralPath $extra) {
            Remove-Item -LiteralPath $extra -Force
        }
    }

    Write-Host "Publishing $rid to $output"
    & $dotnet publish $appProject -c $Configuration -r $rid --self-contained true -p:WindowsAppSDKSelfContained=true "-p:Version=$($stamp.Informational)" "-p:AssemblyVersion=$($stamp.Assembly)" "-p:FileVersion=$($stamp.Assembly)" "-p:InformationalVersion=$($stamp.Informational)" -p:IncludeSourceRevisionInInformationalVersion=false -o $output --nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $exe = Join-Path $output 'DivoomPcMonitor.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Publish finished without $exe"
    }

    Get-ChildItem -LiteralPath $output -Recurse -File |
        Where-Object { $_.Extension -eq '.pdb' -or $_.Name -eq 'createdump.exe' -or $_.Name -eq 'RestartAgent.exe' } |
        Remove-Item -Force

    Compress-Archive -Path $output -DestinationPath $zip
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
    $hashLine = "{0}  {1}" -f $hash, (Split-Path -Leaf $zip)
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($hashFile, $hashLine + "`n", $utf8)

    $item = Get-Item -LiteralPath $exe
    $zipItem = Get-Item -LiteralPath $zip
    Write-Host ("Ready: {0}" -f $item.FullName)
    Write-Host ("File version: {0}" -f $item.VersionInfo.FileVersion)
    Write-Host ("Product version: {0}" -f $item.VersionInfo.ProductVersion)
    Write-Host ("Zip: {0} ({1:N1} MB)" -f $zipItem.FullName, ($zipItem.Length / 1MB))
    Write-Host ("SHA256: {0}" -f $hashLine)
}

Write-Host 'The exe and the runtime files are in the same folder.'
