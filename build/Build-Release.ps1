[CmdletBinding()]
param(
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet.exe -ErrorAction Stop).Source
}
$appProject = Join-Path $root 'src\DisplaySourcePilot.App\DisplaySourcePilot.App.csproj'
$tests = Join-Path $root 'tests\DisplaySourcePilot.Core.Tests\DisplaySourcePilot.Core.Tests.csproj'
$publishRoot = Join-Path $root 'artifacts\publish'
$solution = Join-Path $root 'DisplaySourcePilot.slnx'
$nugetConfig = Join-Path $root 'NuGet.Config'

& $dotnet restore $solution --configfile $nugetConfig --nologo
if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }

& $dotnet run --project $tests --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }

foreach ($target in @(
    @{ Runtime = 'win-x64'; Platform = 'x64' },
    @{ Runtime = 'win-arm64'; Platform = 'ARM64' }
)) {
    $output = Join-Path $publishRoot $target.Runtime
    $resolvedOutput = [IO.Path]::GetFullPath($output)
    $resolvedPublishRoot = [IO.Path]::GetFullPath($publishRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedOutput.StartsWith($resolvedPublishRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected publish path: $resolvedOutput"
    }
    if (Test-Path -LiteralPath $resolvedOutput) {
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }

    & $dotnet publish $appProject `
        --configuration Release `
        --runtime $target.Runtime `
        --self-contained true `
        --output $output `
        --no-restore `
        -p:Platform=$($target.Platform)
    if ($LASTEXITCODE -ne 0) { throw "Publishing $($target.Runtime) failed." }
}

if (-not $SkipInstaller) {
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $innoCandidates = @(
        (Join-Path $root '.tools\inno\ISCC.exe'),
        (Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe'),
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    $inno = $innoCandidates | Select-Object -First 1
    if (-not $inno) {
        throw 'Inno Setup 6 is required to build the installer. Install it or run with -SkipInstaller.'
    }

    [xml]$project = Get-Content -LiteralPath $appProject
    $version = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "The application version is missing or invalid: $version"
    }

    & $inno "/DProductVersion=$version" (Join-Path $root 'installer\DisplaySourcePilot.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
