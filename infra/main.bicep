// App Service (Linux) hosting for the Microsoft To Do MCP server.
// The Entra client secret is set by setup.ps1 straight into app settings, not passed through here.

@description('Globally unique web app name; becomes https://<name>.azurewebsites.net')
param appName string

param location string = resourceGroup().location

@description('F1 is free but sleeps when idle (first call after a nap is slow). B1 stays warm, about US$13/month.')
@allowed(['F1', 'B1'])
param sku string = 'F1'

@description('Application (client) id of the Entra app registration.')
param entraClientId string

@description('Your sign-in name or Entra object id; only this account may use the connector.')
param allowedUser string

@description('IANA or Windows time zone id for due dates and reminders.')
param timeZone string = 'America/Los_Angeles'

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${appName}-plan'
  location: location
  kind: 'linux'
  sku: { name: sku }
  properties: { reserved: true }
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: sku != 'F1'
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      healthCheckPath: '/healthz'
      appSettings: [
        { name: 'AzureAd__ClientId', value: entraClientId }
        { name: 'AzureAd__TenantId', value: 'common' }
        { name: 'TodoMcp__PublicBaseUrl', value: 'https://${appName}.azurewebsites.net' }
        { name: 'TodoMcp__AllowedUsers__0', value: allowedUser }
        { name: 'TodoMcp__TimeZone', value: timeZone }
      ]
    }
  }
}

output url string = 'https://${site.properties.defaultHostName}'
output mcpUrl string = 'https://${site.properties.defaultHostName}/mcp'
