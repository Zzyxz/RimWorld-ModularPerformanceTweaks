$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "ColonyPerformanceTweaks.csproj"

foreach ($version in "1.7", "1.6") {
    Write-Host "Building RimWorld $version ..."
    dotnet build $project -c Release "-p:RimWorldVersion=$version" "-p:BaseIntermediateOutputPath=obj/$version/"
    if ($LASTEXITCODE -ne 0) { throw "Build for RimWorld $version failed" }
}
