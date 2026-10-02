param([string]$BaseUrl = "http://localhost:5100", [switch]$Learn)
$ErrorActionPreference = "Stop"
if (-not $env:SECURE_INTELLIGENCE_API_KEY) { throw "Set SECURE_INTELLIGENCE_API_KEY first." }
$headers = @{ "X-Internal-Api-Key" = $env:SECURE_INTELLIGENCE_API_KEY }
$request = @{
    application = "LineDesigner"
    issueType = "ReadyForQuote"
    description = "Synthetic test: ready line with incomplete preparation"
    signals = @{ readyForQuote = "true"; bomItemCount = "0"; systemizationConfigured = "false"; dbLatencyMs = "2200" }
}
function Diagnose {
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/diagnose" -Headers $headers -ContentType "application/json" -Body ($request | ConvertTo-Json -Depth 5) | ConvertTo-Json -Depth 8
}
Diagnose
if ($Learn) {
    if (-not $env:SECURE_INTELLIGENCE_LEARNING_API_KEY) { throw "Set the separate learning key for this synthetic learning demo." }
    $feedback = @{ request = $request; confirmedCause = "Systemization was not configured"; resolution = "Configure systemization and run validation"; approvedForLearning = $true }
    $learningHeaders = @{ "X-Internal-Api-Key" = $env:SECURE_INTELLIGENCE_LEARNING_API_KEY }
    $result = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/feedback" -Headers $learningHeaders -ContentType "application/json" -Body ($feedback | ConvertTo-Json -Depth 5)
    $result | ConvertTo-Json
    Diagnose
    Write-Host "Remove the synthetic case when finished: DELETE $BaseUrl/api/v1/cases/$($result.caseId) using the learning key."
}
