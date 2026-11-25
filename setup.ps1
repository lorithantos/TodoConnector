<#
.SYNOPSIS
  The Entra half of setting up the connector: the app registration (-Stage App) and its trust
  in the web app's managed identity (-Stage Trust). No client secret is created anywhere.

.DESCRIPTION
  The connector's Entra app has no client secret. Its credential is the user-assigned managed
  identity "LtTodoConnector-identity" that infra/main.bicep creates: the app trusts that
  identity through a federated identity credential (issuer
  https://login.microsoftonline.com/<home tenant id>/v2.0, subject = the identity's principal
  id, audience api://AzureADTokenExchange), and the web app signs its token requests with an
  assertion obtained from the identity. Only a user-assigned identity can be such a credential,
  and it must be in the same tenant as the app registration.

  The whole flow, in order:
    1. ./setup.ps1 -Stage App
         Creates or reuses the app registration and prints its appId.
    2. azure_whatif, then azure_deploy (Janet MCP tools, so each live change is prompted) of
       infra/main.bicep to resource group Lori_Photography, with the override
       entraClientId=<appId> (and allowedUsers, once known). Read identityPrincipalId from the
       deployment's outputs.
    3. ./setup.ps1 -Stage Trust -IdentityPrincipalId <identityPrincipalId>
         Adds the federated identity credential to the app registration.
    4. Deploy the code: Invoke-BuildDeploy.ps1 -ManifestPath deploy-manifest.json.
  This script does not touch Azure resources: no resource group, no Bicep, no code deploy.

  -Stage App, idempotent by display name:
    An Entra app registration that accepts personal Microsoft accounts and work/school
    accounts, with v2 access tokens, the Application ID URI api://<appId>, an access_as_user
    scope, the delegated Microsoft Graph permissions Tasks.ReadWrite, User.Read and
    offline_access, and Claude's two OAuth callbacks as reply URLs. A reused app keeps its
    existing scope. It warns if the app still carries client secrets from the old setup; it
    does not remove them.

  -Stage Trust, idempotent:
    Reuses a federated credential that already has this issuer, subject and audience. Refuses,
    changing nothing, when a credential with the same name trusts something else, or when a
    differently named credential has this issuer and subject with another audience. Also
    refuses when -IdentityPrincipalId is not a service principal in the signed-in tenant, or is
    the identity's client id rather than its principal (object) id.

  Needs: Azure CLI signed in (az login) to the tenant that should own the app registration and
  that holds the managed identity.

.PARAMETER Stage
  App creates or reuses the app registration. Trust adds the federated identity credential.

.PARAMETER IdentityPrincipalId
  -Stage Trust only: the managed identity's principal (object) id, the Bicep output
  identityPrincipalId. Not its client id.

.PARAMETER DisplayName
  Display name of the app registration, which is how both stages find it.

.PARAMETER CredentialName
  Name of the federated identity credential on the app registration.

.EXAMPLE
  ./setup.ps1 -Stage App

.EXAMPLE
  ./setup.ps1 -Stage Trust -IdentityPrincipalId 00000000-0000-0000-0000-000000000000
#>
param(
    [Parameter(Mandatory)] [ValidateSet('App', 'Trust')] [string] $Stage,
    [guid] $IdentityPrincipalId,
    [string] $DisplayName = 'Claude To Do connector',
    [string] $CredentialName = 'LtTodoConnector-identity'
)
$ErrorActionPreference = 'Stop'
$graphAppId = '00000003-0000-0000-c000-000000000000'
$exchangeAudience = 'api://AzureADTokenExchange'
function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Az { $out = & az @args; if ($LASTEXITCODE) { throw "az $args failed" }; $out }

function Find-AppId {
    # The app registration by display name: $null when there is none, a refusal when the name
    # is ambiguous, because picking one of several would configure the wrong app.
    $ids = @(Az ad app list --display-name $DisplayName --query '[].appId' -o json | ConvertFrom-Json)
    if ($ids.Count -gt 1) {
        throw "$($ids.Count) app registrations are named '$DisplayName' ($($ids -join ', ')). Rename or delete the extras, or pass -DisplayName."
    }
    if ($ids.Count -eq 1) { return $ids[0] }
    return $null
}

if ($Stage -eq 'Trust' -and -not $IdentityPrincipalId) {
    throw '-Stage Trust needs -IdentityPrincipalId: the identityPrincipalId output of the infra/main.bicep deployment.'
}

if ($Stage -eq 'App') {
    Step "Entra app registration '$DisplayName'"
    $appId = Find-AppId
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
    $existingScope = Az ad app show --id $appId --query "api.oauth2PermissionScopes[?value=='access_as_user'].id | [0]" -o tsv
    if (-not $existingScope) {
        $body = @{
            identifierUris = @("api://$appId")
            api = @{
                requestedAccessTokenVersion = 2
                oauth2PermissionScopes = @(@{
                    id = [guid]::NewGuid().ToString(); value = 'access_as_user'; type = 'User'; isEnabled = $true
                    adminConsentDisplayName = 'Use the To Do connector'
                    adminConsentDescription = 'Lets Claude manage your Microsoft To Do tasks through this connector.'
                    userConsentDisplayName = 'Use the To Do connector'
                    userConsentDescription = 'Lets Claude manage your Microsoft To Do tasks through this connector.'
                })
            }
        } | ConvertTo-Json -Depth 6 -Compress
        $tmp = New-TemporaryFile; Set-Content $tmp $body
        try {
            Az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$objectId" `
                --headers 'Content-Type=application/json' --body "@$tmp" | Out-Null
        } finally { Remove-Item $tmp }
    } else {
        Write-Host 'Scope access_as_user already exists; leaving it alone.'
    }

    Step 'Microsoft Graph delegated permissions: Tasks.ReadWrite, User.Read, offline_access'
    $scopes = Az ad sp show --id $graphAppId --query 'oauth2PermissionScopes[].{v:value,id:id}' -o json | ConvertFrom-Json
    foreach ($name in 'Tasks.ReadWrite', 'User.Read', 'offline_access') {
        $id = ($scopes | Where-Object v -eq $name).id
        Az ad app permission add --id $appId --api $graphAppId --api-permissions "$id=Scope" 2>$null | Out-Null
    }

    $secrets = @(Az ad app credential list --id $appId --query '[].displayName' -o json | ConvertFrom-Json)
    if ($secrets.Count -gt 0) {
        Write-Warning "The app still has $($secrets.Count) client secret(s) ($($secrets -join ', ')). Nothing in Azure uses them now; remove them with 'az ad app credential delete' once the managed identity works."
    }

    Write-Host "`nApp registration ready. appId: $appId" -ForegroundColor Green
    Write-Host 'Next: azure_whatif, then azure_deploy, of infra/main.bicep to resource group Lori_Photography'
    Write-Host "with the override entraClientId=$appId; then ./setup.ps1 -Stage Trust -IdentityPrincipalId <identityPrincipalId output>."
    return
}

# ---- Stage Trust -------------------------------------------------------------

$principalId = $IdentityPrincipalId.ToString()

Step "Entra app registration '$DisplayName'"
$appId = Find-AppId
if (-not $appId) { throw "No app registration is named '$DisplayName'. Run ./setup.ps1 -Stage App first." }
Write-Host "App $appId"

Step "Managed identity principal $principalId"
# A user-assigned identity is a service principal in its tenant. Looking it up here proves it is
# in the signed-in tenant (the app's), and catches the identity's client id passed by mistake:
# 'az ad sp show' accepts an appId too, and then answers with a different object id.
$spId = & az ad sp show --id $principalId --query id -o tsv 2>$null
if ($LASTEXITCODE -or -not $spId) {
    throw "No service principal $principalId in the signed-in tenant. Pass the identityPrincipalId output, and sign in to the tenant that holds both the identity and the app."
}
if ($spId -ne $principalId) {
    throw "$principalId is a client id; its principal (object) id is $spId. Pass the identityPrincipalId output, not identityClientId."
}
$tenantId = Az account show --query tenantId -o tsv
$issuer = "https://login.microsoftonline.com/$tenantId/v2.0"
Write-Host "Issuer $issuer"

Step "Federated identity credential '$CredentialName'"
$existing = @(Az ad app federated-credential list --id $appId -o json | ConvertFrom-Json)
$isMatch = {
    param($c)
    $c.issuer -eq $issuer -and $c.subject -eq $principalId -and
        @($c.audiences).Count -eq 1 -and @($c.audiences)[0] -eq $exchangeAudience
}
$same = @($existing | Where-Object { & $isMatch $_ })
if ($same.Count -gt 0) {
    Write-Host "Reusing credential '$($same[0].name)': it already trusts this identity."
} else {
    $named = @($existing | Where-Object name -eq $CredentialName)
    if ($named.Count -gt 0) {
        $n = $named[0]
        throw "A credential named '$CredentialName' already exists and trusts something else (issuer $($n.issuer), subject $($n.subject), audiences $(@($n.audiences) -join ', ')). Changing nothing. Delete it with 'az ad app federated-credential delete --id $appId --federated-credential-id $($n.id)' if it is stale, or pass another -CredentialName."
    }
    $clash = @($existing | Where-Object { $_.issuer -eq $issuer -and $_.subject -eq $principalId })
    if ($clash.Count -gt 0) {
        throw "Credential '$($clash[0].name)' already has this issuer and subject but audiences $(@($clash[0].audiences) -join ', '), not $exchangeAudience. Changing nothing; Entra allows one credential per issuer and subject."
    }
    $body = @{
        name = $CredentialName
        issuer = $issuer
        subject = $principalId
        audiences = @($exchangeAudience)
        description = 'The LtTodoConnector web app''s user-assigned managed identity, used instead of a client secret.'
    } | ConvertTo-Json -Compress
    $tmp = New-TemporaryFile; Set-Content $tmp $body
    try {
        Az ad app federated-credential create --id $appId --parameters $tmp.FullName | Out-Null
    } finally { Remove-Item $tmp }
    Write-Host "Created credential '$CredentialName'."
}

Write-Host "`nTrust in place." -ForegroundColor Green
Write-Host 'Next: deploy the code with Invoke-BuildDeploy.ps1 -ManifestPath deploy-manifest.json, then add the'
Write-Host 'connector in Claude (Settings > Connectors > Add custom connector, the mcpUrl output).'
