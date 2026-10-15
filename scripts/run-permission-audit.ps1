param(
    [string]$ResultsDirectory = "backend/tests/Project.Tests/TestResults"
)

$ErrorActionPreference = "Stop"
$project = "backend/tests/Project.Tests/Project.Tests.csproj"
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null

dotnet test $project --filter "FullyQualifiedName~PermissionApiAuthorizationTests|FullyQualifiedName~SecurityFixTests" `
    --results-directory $ResultsDirectory `
    --logger "console;verbosity=detailed" `
    --logger "trx;LogFileName=permission-audit.trx"

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Permission audit passed. TRX report: $ResultsDirectory/permission-audit.trx"
