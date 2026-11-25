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
  * `/oauth/token` relays the code or refresh token to Entra and adds the client secret.
  It stores nothing. Entra does the sign-in, consent and PKCE check, and only redirects to the
  reply URLs on the app registration (Claude's callbacks).
* The token Claude gets is for this app. `/mcp` validates it with Microsoft.Identity.Web, checks
  the caller against `TodoMcp:AllowedUsers`, and swaps it for a Graph token (`Tasks.ReadWrite`)
  with the on-behalf-of flow.
* Only accounts in `TodoMcp:AllowedUsers` (sign-in name or object id) get in. If the list is
  empty, nobody does, and the log says the refused caller's oid and name.

## Setup (one time)

Needs the Azure CLI signed in to the right tenant and subscription, and the .NET 10 SDK.

```powershell
./setup.ps1 -AppName todo-mcp-<something-unique> -AllowedUser you@outlook.com
```

It creates the app registration, a resource group `rg-todo-mcp`, an F1 (free) Linux App Service
plan and web app, puts the client secret straight into the web app's settings, and deploys.
Read the header of `setup.ps1` for the details and parameters (`-Sku B1` keeps it warm).

Then in Claude: **Settings > Connectors > Add custom connector**, paste
`https://<app-name>.azurewebsites.net/mcp`, leave the OAuth client id and secret empty, and
connect. You'll sign in with Microsoft and consent to "Read and write your tasks".

Later deploys: `./deploy.ps1 -AppName <app-name>`.

## Local development

```powershell
dotnet user-secrets --project src/TodoMcp set AzureAd:ClientId <client-id>
dotnet user-secrets --project src/TodoMcp set AzureAd:ClientSecret <secret>
dotnet user-secrets --project src/TodoMcp set "TodoMcp:AllowedUsers:0" you@outlook.com
dotnet test
```

Claude can't reach localhost, so for a real end-to-end run against Claude use the deployed app
(or a dev tunnel). The tests cover the OAuth facade and drive the real MCP endpoint against a
fake Graph.

## Configuration

| Setting | Default | Notes |
| --- | --- | --- |
| `AzureAd:ClientId` | | App registration's client id. |
| `AzureAd:ClientSecret` | | App setting / user-secrets only. Key Vault reference is a sensible upgrade. |
| `AzureAd:TenantId` | `common` | `consumers` if you only ever use a personal account. |
| `TodoMcp:AllowedUsers` | empty | Sign-in names or object ids. |
| `TodoMcp:TimeZone` | `America/Los_Angeles` | IANA or Windows id. |
| `TodoMcp:PublicBaseUrl` | from request | Set by the Bicep template. |

## Guesses to verify on first run

1. **On-behalf-of with a personal Microsoft account.** Microsoft has a sample for exactly this,
   but it's the step most likely to need a tweak. Symptom: tools fail with a Graph token error.
2. **Combined consent through `/.default`.** The one sign-in should also consent to
   Tasks.ReadWrite. If tool calls say consent is missing, reconnect the connector once.
3. **Claude accepting the facade's fixed client id** from dynamic registration.
4. **`$filter=status ne 'completed'`** on To Do tasks (documented, but untested here).
5. **Time zone** defaults to Pacific. Change `TodoMcp__TimeZone` if that's wrong.
6. **Bicep** (`DOTNETCORE|10.0` on F1 Linux) has not been deployed yet.
