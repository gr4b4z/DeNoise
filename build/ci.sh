#!/usr/bin/env bash
# Local mirror of the CI pipeline. Requires .NET 10 SDK and a PostgreSQL reachable via ALERTHUB_TEST_CONNECTION
# (or Docker for Testcontainers).
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore
dotnet build --no-restore -c Release
dotnet format --verify-no-changes --no-restore
dotnet test --no-build -c Release --filter Category=Unit
dotnet test --no-build -c Release --filter Category=Integration
