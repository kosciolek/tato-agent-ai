param(
    [ValidateSet("x86", "x64", "Both")]
    [string]$Platform = "Both",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot "AgentReadonly.csproj"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe was not found. Install Visual Studio Build Tools 2022 with the .NET desktop build tools."
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) {
    throw "MSBuild was not found. Install Visual Studio Build Tools 2022 with MSBuild."
}

$platforms = if ($Platform -eq "Both") { @("x86", "x64") } else { @($Platform) }

foreach ($targetPlatform in $platforms) {
    & $msbuild $project /p:Configuration=$Configuration /p:Platform=$targetPlatform /nologo
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed for $targetPlatform."
    }

    $outputDir = Join-Path $PSScriptRoot "bin\$targetPlatform\$Configuration"
    Copy-Item (Join-Path $PSScriptRoot "CONTEXT.example.md") (Join-Path $outputDir "CONTEXT.example.md") -Force
    Write-Host "Built agent-readonly $targetPlatform at $outputDir"
}
