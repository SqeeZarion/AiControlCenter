#!/usr/bin/env sh
set -eu

output_directory="${1:-./secrets/identity}"
script_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
dotnet run --project "$script_directory/IdentityKeyGenerator/IdentityKeyGenerator.csproj" -- "$output_directory"
