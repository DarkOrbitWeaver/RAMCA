# Verified PowerShell setups for Claude Code, Gemini CLI, LocalIdeBridge CLI, and raw HTTPS API.
# Set your access key in the current shell session (never commit it to Git):
#   $env:LOCALIDEBRIDGE_KEY = "<paste-from-LocalIdeBridge-Copy-Key>"

$Endpoint = if ($env:LOCALIDEBRIDGE_ENDPOINT) { $env:LOCALIDEBRIDGE_ENDPOINT } else { "http://127.0.0.1:43170" }

# 1. Claude Code (Streamable HTTP)
# claude mcp add --transport http localidebridge "$Endpoint/mcp" --header "Authorization: Bearer $env:LOCALIDEBRIDGE_KEY"

# 2. Direct authenticated HTTP/HTTPS API test call
$Headers = @{ Authorization = "Bearer $env:LOCALIDEBRIDGE_KEY" }
$Info = Invoke-RestMethod -Uri "$Endpoint/api/v1/info" -Method Get -Headers $Headers
Write-Host "Connected to $($Info.service) v$($Info.version) | Workspace: $($Info.activeWorkspace) | Tools: $($Info.toolCount)"

$Status = Invoke-RestMethod `
    -Uri "$Endpoint/api/v1/tools/workspace_status/invoke" `
    -Method Post `
    -Headers $Headers `
    -ContentType "application/json" `
    -Body "{}"
$Status | ConvertTo-Json -Depth 6
