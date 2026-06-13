$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "NetClean\NetClean.csproj"
$output = Join-Path $PSScriptRoot "publish"

dotnet publish $project -c Release -r win-x64 --self-contained true -o $output `
  /p:PublishSingleFile=true `
  /p:PublishTrimmed=false `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -ne 0) {
  throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Remove-Item -LiteralPath (Join-Path $output "NetGuard.pdb") -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $output "NetClean.exe") -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $output "NetClean.pdb") -ErrorAction SilentlyContinue
Write-Host "Published: $output\NetGuard.exe"
