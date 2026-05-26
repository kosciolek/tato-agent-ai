param(
    [ValidateSet("x86", "x64", "Both")]
    [string]$Platform = "x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
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
$commit = try { (git -C $repoRoot rev-parse HEAD).Trim() } catch { "local" }
$builtAtUtc = [DateTime]::UtcNow.ToString("o")

foreach ($targetPlatform in $platforms) {
    & $msbuild $project /p:Configuration=$Configuration /p:Platform=$targetPlatform /nologo
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed for $targetPlatform."
    }

    $outputDir = Join-Path $PSScriptRoot "bin\$targetPlatform\$Configuration"
    Copy-Item (Join-Path $PSScriptRoot "CONTEXT.example.md") (Join-Path $outputDir "CONTEXT.example.md") -Force
    @{
        commit = $commit
        built_at_utc = $builtAtUtc
        asset_name = "agent-readonly-windows-$targetPlatform.zip"
    } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $outputDir "build-info.json")
    Write-Host "Built agent-readonly $targetPlatform at $outputDir"
}
