<#
.SYNOPSIS
  One-time setup: Entra app registration + App Service, then deploys the server.

.DESCRIPTION
  Creates (in this order, and prints each step):
    1. An Entra app registration "Claude To Do connector" that accepts personal Microsoft accounts
       and work/school accounts, with Claude's OAuth callbacks as reply URLs and the delegated
       Microsoft Graph permission Tasks.ReadWrite.
    2. A client secret for it, written straight into the web app's settings (never printed).
    3. A resource group, a Linux App Service plan (F1 by default) and a web app, via infra/main.bicep.
    4. A build of src/TodoMcp, zip-deployed to the web app.
  Re-running it reuses the app registration and resource group if they exist.

  Needs: Azure CLI signed in (az login) to the tenant and subscription you want, .NET 10 SDK.

.EXAMPLE
  ./setup.ps1 -AppName todo-mcp-lori -AllowedUser you@outlook.com
#>
param(
    [Parameter(Mandatory)] [string] $AppName,
    [Parameter(Mandatory)] [string] $AllowedUser,
    [string] $ResourceGroup = 'rg-todo-mcp',
    [string] $Location = 'westus2',
    [ValidateSet('F1', 'B1')] [string] $Sku = 'F1',
    [string] $TimeZone = 'America/Los_Angeles',
    [string] $DisplayName = 'Claude To Do connector'
)
$ErrorActionPreference = 'Stop'
$graphAppId = '00000003-0000-0000-c000-000000000000'
function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Az { $out = & az @args; if ($LASTEXITCODE) { throw "az $args failed" }; $out }

Step "Entra app registration '$DisplayName'"
$appId = Az ad app list --display-name $DisplayName --query '[0].appId' -o tsv
if (-not $appId) {
    # Personal accounts require v2 access tokens, and that must be set before the audience changes.
    $appId = Az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg `
        --web-redirect-uris 'https://claude.ai/api/mcp/auth_callback' 'https://claude.com/api/mcp/auth_callback' `
        --query appId -o tsv
    Az ad app update --id $appId --set api.requestedAccessTokenVersion=2 | Out-Null
    Az ad app update --id $appId --sign-in-audience AzureADandPersonalMicrosoftAccount | Out-Null
    Write-Host "Created app $appId"
} else {
    Write-Host "Reusing app $appId"
}
$objectId = Az ad app show --id $appId --query id -o tsv

Step 'Application ID URI and the access_as_user scope'
$scopeId = [guid]::NewGuid().ToString()
$existingScope = Az ad app show --id $appId --query "api.oauth2PermissionScopes[?value=='access_as_user'].id | [0]" -o tsv
if (-not $existingScope) {
    $body = @{
        identifierUris = @("api://$appId")
        api = @{
            requestedAccessTokenVersion = 2
            oauth2PermissionScopes = @(@{
                id = $scopeId; value = 'access_as_user'; type = 'User'; isEnabled = $true
                adminConsentDisplayName = 'Use the To Do connector'
                adminConsentDescription = 'Lets Claude manage your Microsoft To Do tasks through this connector.'
                userConsentDisplayName = 'Use the To Do connector'
                userConsentDescription = 'Lets Claude manage your Microsoft To Do tasks through this connector.'
            })
        }
    } | ConvertTo-Json -Depth 6 -Compress
    $tmp = New-TemporaryFile; Set-Content $tmp $body
    Az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$objectId" `
        --headers 'Content-Type=application/json' --body "@$tmp" | Out-Null
    Remove-Item $tmp
}

Step 'Microsoft Graph delegated permissions: Tasks.ReadWrite, User.Read, offline_access'
$scopes = Az ad sp show --id $graphAppId --query 'oauth2PermissionScopes[].{v:value,id:id}' -o json | ConvertFrom-Json
foreach ($name in 'Tasks.ReadWrite', 'User.Read', 'offline_access') {
    $id = ($scopes | Where-Object v -eq $name).id
    Az ad app permission add --id $appId --api $graphAppId --api-permissions "$id=Scope" 2>$null | Out-Null
}

Step "Resource group $ResourceGroup and App Service ($Sku) via infra/main.bicep"
Az group create --name $ResourceGroup --location $Location | Out-Null
$deployment = Az deployment group create --resource-group $ResourceGroup --template-file "$PSScriptRoot/infra/main.bicep" `
    --parameters appName=$AppName sku=$Sku entraClientId=$appId allowedUser=$AllowedUser timeZone=$TimeZone `
    --query properties.outputs -o json | ConvertFrom-Json

Step 'Client secret -> web app setting AzureAd__ClientSecret (not printed)'
$hasSecret = Az webapp config appsettings list -g $ResourceGroup -n $AppName --query "[?name=='AzureAd__ClientSecret'] | length(@)" -o tsv
if ($hasSecret -eq '0') {
    $secret = Az ad app credential reset --id $appId --display-name 'app-service' --years 1 --append --query password -o tsv
    Az webapp config appsettings set -g $ResourceGroup -n $AppName --settings "AzureAd__ClientSecret=$secret" | Out-Null
    Remove-Variable secret
} else {
    Write-Host 'Secret already set; leaving it alone.'
}

Step 'Build and deploy'
$publish = Join-Path ([IO.Path]::GetTempPath()) "todo-mcp-publish"
$zip = "$publish.zip"
Remove-Item $publish, $zip -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$PSScriptRoot/src/TodoMcp" -c Release -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }
Compress-Archive -Path "$publish/*" -DestinationPath $zip
Az webapp deploy -g $ResourceGroup -n $AppName --src-path $zip --type zip | Out-Null

Write-Host "`nDone." -ForegroundColor Green
Write-Host "Connector URL for Claude: $($deployment.mcpUrl.value)"
Write-Host 'In Claude: Settings > Connectors > Add custom connector, paste that URL, leave the OAuth fields empty.'
