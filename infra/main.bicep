// The Microsoft To Do MCP connector: a Windows web app on the shared App Service plan, with a
// user-assigned managed identity that the Entra app registration trusts as its credential, so
// no client secret exists anywhere.
//
// The plan is NOT created here. It is ImageSelectorTest20240604205112Plan, described in
// C:\repos\ImageSelectorV2\infra\shared-plan.bicep (an interim home; it moves to a dedicated
// infra repo later -- JanetHome thread item "Untracked Azure infra", area infra). It is
// referenced as an existing resource, so that move changes nothing here.
//
// The Entra app registration and its federated credential are set up by setup.ps1, which
// reads this template's identityPrincipalId output: the credential's subject is that id.

@description('Globally unique web app name; becomes https://<name>.azurewebsites.net')
param appName string = 'LtTodoConnector'

@description('The shared App Service plan this app runs on.')
param planName string = 'ImageSelectorTest20240604205112Plan'

@description('Resource group of the shared plan. A site lives beside its plan.')
param planResourceGroup string = resourceGroup().name

@description('Region of the shared plan; the site must be in it. Not readable from the existing plan at deployment start (BCP120), so it is stated, matching ImageSelectorV2 infra\\shared-plan.bicep.')
param planLocation string = 'westus'

@description('Region of resources that need not follow the plan (the managed identity).')
param location string = resourceGroup().location

@description('Application (client) id of the Entra app registration.')
param entraClientId string

@description('Who may use the connector: "<tenant id>:<object id>" entries. Empty lets nobody in; the refusal log prints the line to add.')
param allowedUsers array = []

@description('IANA or Windows time zone id for due dates and reminders.')
param timeZone string = 'America/Los_Angeles'

resource plan 'Microsoft.Web/serverfarms@2024-04-01' existing = {
  name: planName
  scope: resourceGroup(planResourceGroup)
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${appName}-identity'
  location: location
}

var baseSettings = [
  { name: 'AzureAd__ClientId', value: entraClientId }
  { name: 'AzureAd__TenantId', value: 'common' }
  // The managed identity is the app's credential (workload identity federation): Microsoft.Identity.Web
  // exchanges an identity token for a client assertion. No ClientSecret setting exists.
  { name: 'AzureAd__ClientCredentials__0__SourceType', value: 'SignedAssertionFromManagedIdentity' }
  { name: 'AzureAd__ClientCredentials__0__ManagedIdentityClientId', value: identity.properties.clientId }
  { name: 'AzureAd__ClientCredentials__0__TokenExchangeUrl', value: 'api://AzureADTokenExchange/.default' }
  { name: 'TodoMcp__PublicBaseUrl', value: 'https://${appName}.azurewebsites.net' }
  { name: 'TodoMcp__TimeZone', value: timeZone }
]

var allowedUserSettings = [for (entry, i) in allowedUsers: {
  name: 'TodoMcp__AllowedUsers__${i}'
  value: entry
}]

resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  // A site must be in its plan's region. The plan is in westus while Lori_Photography itself is
  // westus3, so resourceGroup().location put the site in the wrong region and the first deploy
  // (2026-11-25) failed with "Cannot find serverFarm". The identity can live anywhere.
  location: planLocation
  kind: 'app'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      metadata: [
        { name: 'CURRENT_STACK', value: 'dotnet' }
      ]
      use32BitWorkerProcess: false
      alwaysOn: true // free on a dedicated plan: billing is per instance, not per app
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      healthCheckPath: '/healthz'
      appSettings: concat(baseSettings, allowedUserSettings)
    }
  }
}

// Application logging to the file system, read with `az webapp log tail`: the allow-list bootstrap
// copies the refused caller's tid:oid from there. Filesystem application logging switches itself
// off after 12 hours on Windows, which suits a bootstrap; turn it back on to debug later.
resource logs 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: site
  name: 'logs'
  properties: {
    applicationLogs: {
      fileSystem: {
        level: 'Warning'
      }
    }
    detailedErrorMessages: {
      enabled: false
    }
    failedRequestsTracing: {
      enabled: false
    }
    httpLogs: {
      fileSystem: {
        enabled: false
      }
    }
  }
}

output url string = 'https://${site.properties.defaultHostName}'
output mcpUrl string = 'https://${site.properties.defaultHostName}/mcp'

@description('Subject of the federated identity credential setup.ps1 adds to the Entra app.')
output identityPrincipalId string = identity.properties.principalId

output identityClientId string = identity.properties.clientId
