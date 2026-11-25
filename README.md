# Microsoft To Do connector for Claude

A small remote MCP server that lets Claude read and manage your Microsoft To Do tasks, so Claude
can nag you about them. ASP.NET Core on .NET 10, hosted on Azure App Service, signing in with an
Entra app registration. Works with a personal Microsoft account or a work/school account.

## Tools

| Tool | What it does |
| --- | --- |
| `get_agenda` | The nag list: overdue, due today, due soon, upcoming reminders, important with no date. Includes today's date and time. |
| `list_task_lists` | Your lists. |
| `list_tasks` | Tasks in one list (by name or id). |
| `create_task` | Adds a task with optional due date, reminder, importance, notes. |
| `update_task` | Changes any of those, clears due date or reminder, or reopens a task. |
| `complete_task` | Marks a task done. |
| `create_task_list` | Makes a new list. |

There is deliberately no delete tool. The server also sends Claude instructions to check the
agenda early in a conversation and bring up anything overdue.

## How sign-in works

```
Claude ──OAuth──> this server's /oauth/* facade ──redirect / relay──> Entra ID (login.microsoftonline.com)
Claude ──MCP + Entra access token──> /mcp ──on-behalf-of──> Microsoft Graph /me/todo
```

* Claude's connector client follows the MCP auth spec: it discovers the authorization server from
  `/.well-known/oauth-protected-resource`, registers itself dynamically and sends an RFC 8707
  `resource` parameter. Entra supports none of that directly (it rejects `resource` on v2 with
  AADSTS9010010), so `Auth/OAuthFacade.cs` is a thin, stateless facade:
  * `/oauth/register` hands Claude a fixed public client id;
  * `/oauth/authorize` redirects to Entra with the real client id and the scope
    `openid offline_access api://<client-id>/.default`, dropping `resource`;
  * `/oauth/token` relays the code or refresh token to Entra and adds the app's credential: in
    Azure a client assertion from the managed identity, in local development a user-secret.
  It stores nothing. Entra does the sign-in, consent and PKCE check, and only redirects to the
  reply URLs on the app registration (Claude's callbacks).
* The token Claude gets is for this app. `/mcp` validates it with Microsoft.Identity.Web, checks
  the caller against `TodoMcp:AllowedUsers`, and swaps it for a Graph token (`Tasks.ReadWrite`)
  with the on-behalf-of flow.
* Only accounts in `TodoMcp:AllowedUsers` get in. Each entry is `<tid>:<oid>`, the caller's
  Entra tenant id and object id (two GUIDs joined by a colon, any case), and a caller gets in
  only when both match one entry. Sign-in names are not accepted: Microsoft's
  [claims validation guidance](https://learn.microsoft.com/entra/identity-platform/claims-validation)
  says to authorize on the immutable `tid` and `oid` together, never on `preferred_username`,
  `upn` or `email`, which are mutable and can be set by a tenant admin. An entry that is not
  two GUIDs joined by a colon stops the app at startup with the entry named. If the list is
  empty, nobody gets in. To add yourself: connect once, get refused, and copy the entry from
  the log line `Refused caller tid:oid=<tid>:<oid> (shown as <name>, display only). Add
  <tid>:<oid> to TodoMcp:AllowedUsers to let them in.` Personal Microsoft accounts all carry
  tid `9188040d-6c67-4c5b-b112-36a304b66dad`.

## Setup (one time)

The connector is the Windows web app `LtTodoConnector` in resource group `Lori_Photography`, on
the existing shared App Service plan `ImageSelectorTest20240604205112Plan` (which also hosts
LtImageSelector and LtRetirement; that plan's Bicep is `C:\repos\ImageSelectorV2\infra\shared-plan.bicep`).
`infra/main.bicep` adds only the web app (Always On) and a user-assigned managed identity,
`LtTodoConnector-identity`. There is no client secret anywhere: the Entra app registration
trusts that identity through a federated identity credential (issuer
`https://login.microsoftonline.com/<home tenant id>/v2.0`, subject the identity's principal id,
audience `api://AzureADTokenExchange`), and the web app proves it is the app with an assertion
from the identity. The identity and the app registration must be in the same tenant.

Needs the Azure CLI signed in to the tenant that owns the app registration and the
subscription, the .NET 10 SDK, and the Janet MCP tools (live Azure changes go through them so
each one is prompted).

1. **App registration.** `./setup.ps1 -Stage App` creates the app registration (or reuses the
   one named "Claude To Do connector") and prints its `appId`. No secret is created.
2. **Infrastructure.** In a Claude session, run `azure_whatif` and read it, then `azure_deploy`,
   both with path `infra/main.bicep`, resource group `Lori_Photography` and the override
   `entraClientId=<appId>`. Leave `allowedUsers` out the first time. Note the outputs
   `identityPrincipalId` and `mcpUrl`.
3. **Trust.** `./setup.ps1 -Stage Trust -IdentityPrincipalId <identityPrincipalId>` adds the
   federated identity credential to the app registration. It is safe to re-run, and refuses
   to touch a same-named credential that trusts something else.
4. **Code.** Deploy as under [Later deploys](#later-deploys).
5. **Connect.** In Claude: **Settings > Connectors > Add custom connector**, paste the `mcpUrl`
   output (`https://lttodoconnector.azurewebsites.net/mcp`), leave the OAuth client id and
   secret empty, and connect. You'll sign in with Microsoft and consent to "Read and write
   your tasks".
6. **Let yourself in.** The allow-list is empty, so that first connection is refused. Copy the
   entry from the app's log line `... Add <tid>:<oid> to TodoMcp:AllowedUsers to let them in.`
   and repeat step 2 with the overrides `entraClientId=<appId>` and
   `allowedUsers=["<tid>:<oid>"]`. Then connect again.

The template sets the web app's whole list of app settings, so every later `azure_deploy` must
pass `entraClientId` and the full `allowedUsers` again; leaving `allowedUsers` out empties the
allow-list. It also clears the `LATEST_BUILD_INFO` stamp until the next code deploy.

## Later deploys

Commit first: the orchestrator refuses a working tree with uncommitted changes. Then

```powershell
& "$env:JanetBase\scripts\Invoke-BuildDeploy.ps1" -ManifestPath ./deploy-manifest.json -Validate
& "$env:JanetBase\scripts\Invoke-BuildDeploy.ps1" -ManifestPath ./deploy-manifest.json
```

(or `/deploy`). `deploy-manifest.json` builds `TodoMcp.slnx`, publishes `src/TodoMcp`, pushes the
zip to `LtTodoConnector` (production; there is no staging slot) with classic zipdeploy, stamps
`LATEST_BUILD_INFO`, restarts, and polls `https://lttodoconnector.azurewebsites.net/healthz`
until it answers 200. A 200 proves an app is running, not which build, and the stamp and the
restart each recycle the site, so one 200 can come from a process that is then replaced:
probe again a minute later, and check `LATEST_BUILD_INFO` and the wwwroot file times rather
than the deployment status.

## Local development

```powershell
dotnet user-secrets --project src/TodoMcp set AzureAd:ClientId <client-id>
dotnet user-secrets --project src/TodoMcp set AzureAd:ClientSecret <secret>
dotnet user-secrets --project src/TodoMcp set "TodoMcp:AllowedUsers:0" <tid>:<oid>
dotnet test
```

A managed identity exists only in Azure, so a local run against real Entra uses a client
secret from user-secrets instead. Make it short-lived, keep it out of Azure's app settings, and
delete it when you are done (`setup.ps1 -Stage App` warns while the app registration has one).

Claude can't reach localhost, so for a real end-to-end run against Claude use the deployed app
(or a dev tunnel). The tests cover the OAuth facade and drive the real MCP endpoint against a
fake Graph.

## Configuration

| Setting | Default | Notes |
| --- | --- | --- |
| `AzureAd:ClientId` | | App registration's client id (`setup.ps1 -Stage App` prints it; the Bicep sets it from `entraClientId`). |
| `AzureAd:ClientSecret` | | Local development only, in user-secrets. Never set in Azure. Used only when no managed identity entry is set; with neither, startup fails naming both. |
| `AzureAd:ClientCredentials:0:SourceType` | | In Azure: `SignedAssertionFromManagedIdentity`, the credential that replaces the secret, for both the token relay and on-behalf-of. Wins over a secret. Set by the Bicep template. |
| `AzureAd:ClientCredentials:0:ManagedIdentityClientId` | | In Azure: the client id of `LtTodoConnector-identity`. Set by the Bicep template, with `TokenExchangeUrl` `api://AzureADTokenExchange/.default`. |
| `AzureAd:TenantId` | `common` | `consumers` if you only ever use a personal account. |
| `TodoMcp:AllowedUsers` | empty | `"<tid>:<oid>"` entries (tenant id and object id GUIDs). Empty lets nobody in; a malformed entry, an email address included, fails startup. Connect once and copy your entry from the refusal in the log. In Azure, the Bicep's `allowedUsers` parameter. |
| `TodoMcp:TimeZone` | `America/Los_Angeles` | IANA or Windows id. The Bicep's `timeZone` parameter. |
| `TodoMcp:PublicBaseUrl` | from request | Set by the Bicep template. |

## Guesses to verify on first run

1. **On-behalf-of with a personal Microsoft account.** Microsoft has a sample for exactly this,
   but it's the step most likely to need a tweak. Symptom: tools fail with a Graph token error.
2. **Combined consent through `/.default`.** The one sign-in should also consent to
   Tasks.ReadWrite. If tool calls say consent is missing, reconnect the connector once.
3. **Claude accepting the facade's fixed client id** from dynamic registration.
4. **`$filter=status ne 'completed'`** on To Do tasks (documented, but untested here).
5. **Time zone** defaults to Pacific. Change `TodoMcp__TimeZone` if that's wrong.
6. **The managed-identity federated credential is accepted for personal-account sign-ins
   through `/common`.** Microsoft does not document this either way. Symptom: the code
   redemption at `/oauth/token` or the on-behalf-of call fails with an AADSTS error about the
   client assertion.
