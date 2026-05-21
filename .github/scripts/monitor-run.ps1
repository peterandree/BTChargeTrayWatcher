param(
    [Parameter(Mandatory=$true)] [string]$RunId,
    [int] $MaxIterations = 60,
    [int] $DelaySeconds = 5
)

$repo = 'peterandree/BTChargeTrayWatcher'
for ($i = 0; $i -lt $MaxIterations; $i++) {
    $resp = gh api repos/$repo/actions/runs/$RunId --jq '{status: .status, conclusion: .conclusion}'
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR calling gh api for run $RunId"
        exit 2
    }
    $obj = $resp | ConvertFrom-Json
    Write-Host "Status: $($obj.status) Conclusion: $($obj.conclusion)"
    if ($obj.status -eq 'completed') { break }
    Start-Sleep -Seconds $DelaySeconds
}

Write-Host "Fetching logs for run $RunId..."
gh run view $RunId --repo $repo --log
