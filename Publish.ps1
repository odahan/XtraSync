<#
.SYNOPSIS
Builds and publishes XtraSync as a self-contained Windows executable.
.DESCRIPTION
Extracts bundled dependencies before startup so WPF can load DirectWriteForwarder.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$publishDirectory = Join-Path $PSScriptRoot 'Publish'
$projectPath = Join-Path $PSScriptRoot 'app\XtraSync.csproj'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "XtraSync source project is missing: $projectPath. Create or restore the application project before publishing."
}

& dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true -m:1 --disable-build-servers '-p:UseSharedCompilation=false' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:IncludeAllContentForSelfExtract=true' --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "XtraSync publication failed with exit code $LASTEXITCODE."
}

$publishedSymbols = Join-Path $publishDirectory 'XtraSync.pdb'
if (Test-Path -LiteralPath $publishedSymbols -PathType Leaf) {
    Remove-Item -LiteralPath $publishedSymbols -Force
}

Write-Host "XtraSync published to $publishDirectory"
