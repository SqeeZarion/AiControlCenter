param(
    [string]$OutputDirectory = "./secrets/identity",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$resolvedOutput = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
$generator = Join-Path $PSScriptRoot "IdentityKeyGenerator/IdentityKeyGenerator.csproj"
$arguments = @("run", "--project", $generator, "--", $resolvedOutput)
if ($Force) { $arguments += "--force" }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "The .NET identity key generator failed." }
