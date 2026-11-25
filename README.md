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
| `get_task` | One task with its steps and their ids. |
| `create_task` | Adds a task with optional due date, reminder, importance, notes, and repeat (daily, weekdays, weekly on chosen days, monthly, yearly, every N). A repeating task with no due date is due today. |
| `update_task` | Changes any of those, clears due date, reminder or repeat, or reopens a task. |
| `complete_task` | Marks a task done. |
| `add_step` / `update_step` / `delete_step` | A task's steps (checklist items): add, rename or tick off, remove. |
| `create_task_list` | Makes a new list. |
| `rename_task_list` | Renames a list. The built-in Tasks and Flagged email lists are refused. |
| `delete_task` | Deletes a task. Destructive. |
| `delete_task_list` | Deletes a list and its tasks. Destructive. The built-in lists are refused. |
| `move_task` | Moves a task and its steps to another list: a copy, then a delete of the original only once the copy is complete. Destructive. |

The destructive tools carry MCP's destructive annotation, and the server's instructions tell Claude
to confirm them with you, naming the task or list, unless you named it yourself in the same
request; and to change a task by updating it, not by deleting and recreating it. The server also
sends Claude instructions to check the agenda early in a conversation and bring up anything
overdue.

## How sign-in works

```
Claude ──OAuth──> this server's /oauth/* facade ──redirect / relay──> Entra ID (login.microsoftonline.com)
Entra ──code or error──> /oauth/callback (logged) ──redirect──> Claude's callback
Claude ──MCP + Entra access token──> /mcp ──on-behalf-of──> Microsoft Graph /me/todo
```

* Claude's connector client follows the MCP auth spec: it discovers the authorization server from
  `/.well-known/oauth-protected-resource`, registers itself dynamically and sends an RFC 8707
  `resource` parameter. Entra supports none of that directly (it rejects `resource` on v2 with
  AADSTS9010010), so `Auth/OAuthFacade.cs` is a thin, stateless facade:
  * `/oauth/register` hands Claude a fixed public client id;
  * `/oauth/authorize` redirects to Entra with the real client id and the scope
    `openid offline_access api://<client-id>/.default`, dropping `resource`. It sends Entra
    this server's own `/oauth/callback` as the redirect URI, and as the state a payload
    protected with ASP.NET Core Data Protection that carries Claude's redirect URI and Claude's
    state and expires after 10 minutes. It also asks for `prompt=select_account`, so Microsoft
    shows the account picker instead of signing in silently with the Windows account, and
    shows its own errors on its page;
  * `/oauth/callback` receives Entra's answer, logs it (an error at Warning with Entra's
    `error_description`, the AADSTS text; a code at Information, without the code), and
    redirects to Claude's callback with the code or the error and Claude's original state. A
    state that does not unprotect, has expired, or names a redirect URI off the allow-list gets
    a 400 and a Warning, and the browser is sent nowhere;
  * `/oauth/token` relays the code or refresh token to Entra and adds the app's credential: in
    Azure a client assertion from the managed identity, in local development a user-secret.
    For a code it sends Entra `/oauth/callback` as the redirect URI, because Entra requires the
    one the authorize request used.
  Every refusal is logged at Warning with its reason; codes, tokens and credentials never are.
  It stores nothing: what the callback needs travels inside the state. Entra does the sign-in,
  consent and PKCE check, and only redirects to the one reply URL on the app registration,
  `https://lttodoconnector.azurewebsites.net/oauth/callback`. Claude's callback page drops its
  query string, so before the callback hop a failed sign-in left no trace anywhere; now the
  App Service log has Entra's answer.
* The state is encrypted with the app's Data Protection key ring. On Windows App Service that
  persists under `%HOME%\ASP.NET\DataProtection-Keys`, network storage every instance in one
  deployment slot shares, unprotected at rest (the app logs "No XML encryptor configured" at
  startup). Each slot has its own key ring, so a sign-in in flight across a slot swap or a new
  site fails at the callback with a logged Warning; connect again.
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
   one named "Claude To Do connector") and prints its `appId`. No secret is created. Its one
   reply URL is `https://lttodoconnector.azurewebsites.net/oauth/callback` (set on every run;
   `-BaseUrl` changes the host, lower case).
2. **Infrastructure.** In a Claude session, run `azure_whatif` and read it, then `azure_deploy`,
   both with path `infra/main.bicep`, resource group `Lori_Photography` and the override
   `entraClientId=<appId>`. Leave `allowedUsers` out the first time. Note the outputs
   `identityPrincipalId` and `mcpUrl`.
3. **Trust.** `./setup.ps1 -Stage Trust -IdentityPrincipalId <identityPrincipalId>` adds the
   federated identity credential to the app registration. It is safe to re-run, and refuses
   to touch a same-named credential that trusts something else.
4. **Code.** Deploy as under [Later deploys](#later-deploys).
5. **Connect.** In Claude: **Settings > Connectors > Add custom connector**, paste
   `https://lttodoconnector.azurewebsites.net/mcp` (lower case, the host the server names
   itself by), leave the OAuth client id and secret empty, and connect. Microsoft
   shows its account picker: pick the account, sign in and consent to "Read and write your
   tasks". If it fails, the reason is in the app's log as a Warning from `/oauth/callback`.
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
| `TodoMcp:PublicBaseUrl` | from request | Set by the Bicep template, lower case: `{PublicBaseUrl}/oauth/callback` must equal the reply URL on the app registration exactly. |

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
