This originates from https://github.com/lukoplt/DefaultAgentBlock/tree/main but has been modified to only block GHCH agents.

# DefaultAgentBlock

A **Dataverse plug-in that blocks the creation of Copilot Studio agents** in a Power Platform
environment — including the agents that the **GitHub Copilot harness** provisions on a user's
behalf — unless the caller is a member of an explicitly allowed team.

Copilot Studio agents are stored as rows in the Dataverse `bot` table. Every path that creates
an agent — the Copilot Studio maker portal, the Power Platform APIs, and the GitHub Copilot
harness that spins up a Copilot Studio agent from a GitHub Copilot session — ends in a `Create`
message on that table. Registering this plug-in synchronously on `bot` Create therefore gives a
single, non-bypassable choke point: makers cannot self-service an agent into the environment,
while a named exception team still can.

Use it to keep an environment (typically the default environment, where every user has a maker
licence) free of ad-hoc agents without disabling Copilot Studio tenant-wide.

## What is in this repository

`DefaultAgentBlock/DefaultAgentBlock.cs` holds three variants. **Only the first is active**; the
other two are kept as commented-out reference implementations.

| Variant | State | Blocks |
| --- | --- | --- |
| `DefaultAgentBlock` | **Active** | *Every* agent create, except for members of the allowed team |
| `BlockCopilotHarnessAgents` (unconditional) | Commented out | Only GitHub Copilot harness agents, no exception |
| `BlockCopilotHarnessAgents` (with team exception) | Commented out | Only GitHub Copilot harness agents, except for members of the allowed team |

The two harness-only variants identify a harness agent by parsing the bot's `configuration`
column as JSON and matching `recognizer.$kind` against `CLICopilotRecognizer` — the recognizer
kind the GitHub Copilot harness stamps on the agents it creates. Anything else is left alone.

Pick the variant that matches your policy: broad block (ship as-is) or harness-only block
(uncomment the variant you want, comment out `DefaultAgentBlock`, and adjust the registered
class name accordingly).

## How the active plug-in decides

1. Reads a single GUID from the step's **unsecure configuration**. The GUID may be either a
   Dataverse `teamid` or the Entra ID (AAD) object ID of the group behind a group team.
2. Resolves the matching team(s) with `service = serviceFactory.CreateOrganizationService(null)`,
   i.e. under the SYSTEM account, so the check never depends on the calling user's read
   privileges on `team` / `teammembership`.
3. Allows the create as soon as either the initiating user or the impersonated user
   (`context.UserId`) is a member of one of those teams.
4. Otherwise throws an `InvalidPluginExecutionException` naming the current environment, which
   surfaces to the maker as an error dialog.

The plug-in **fails closed**: a missing or unparseable configuration GUID, or no team matching
that GUID in the environment, blocks the create rather than letting it through.

---

## Deploying to a tenant

### 0. Prerequisites

* An environment where you have the **System Administrator** role.
* .NET Framework 4.6.2 build tooling (Visual Studio 2019+ with the .NET desktop workload, or
  MSBuild).
* A strong-name key — Dataverse rejects plug-in assemblies that are not strong-named. The
  original `LukasOplt.pfx` is **not** in this repository; signing keys must never be published.
* The **Plug-in Registration Tool** (PRT) and/or the **Power Platform CLI** (`pac`).

Install the tooling:

```powershell
# Plug-in Registration Tool
pac tool prt

# or download it directly from NuGet:
#   Microsoft.CrmSdk.XrmTooling.PluginRegistrationTool
```

### 1. Configure signing and build

Create your own key and point the project at it:

```powershell
# Option A — self-signed .pfx (matches the current csproj layout)
New-SelfSignedCertificate -Type Custom -Subject "CN=YourOrg" `
  -KeyUsage DigitalSignature -CertStoreLocation "Cert:\CurrentUser\My"
# export it to DefaultAgentBlock\YourOrg.pfx, then update the csproj:
#   <AssemblyOriginatorKeyFile>YourOrg.pfx</AssemblyOriginatorKeyFile>

# Option B — plain strong-name key
sn -k DefaultAgentBlock\YourOrg.snk
#   <AssemblyOriginatorKeyFile>YourOrg.snk</AssemblyOriginatorKeyFile>
```

Then restore and build Release:

```powershell
nuget restore DefaultAgentBlock.slnx
msbuild DefaultAgentBlock.slnx /p:Configuration=Release
```

Output: `DefaultAgentBlock\bin\Release\DefaultAgentBlock.dll`.

### 2. Create the exception team

In the target environment, create the team whose members are allowed to create agents, or reuse
an existing one:

* **Dataverse owner team** → copy its `teamid` from the record URL in
  *Power Platform admin center → Environment → Settings → Users + permissions → Teams*.
* **Entra ID group team** (recommended for tenant-wide governance) → create a team of type
  *AAD Security Group* / *AAD Office Group*, and copy either the `teamid` or the group's
  **object ID** from Entra ID. The plug-in accepts both.

Keep this GUID — it is the plug-in's configuration.

> Group-team membership in Dataverse is materialised when a member first accesses the
> environment. Verify that the users you expect actually appear under the team before relying on
> the exception.

### 3. Register the assembly and the step

Using the Plug-in Registration Tool:

1. **Create New Connection** → *Office 365* → sign in as an administrator → pick the environment.
2. **Register → Register New Assembly**.
   * Select `DefaultAgentBlock.dll`.
   * Isolation mode: **Sandbox**.
   * Location: **Database**.
3. Select the registered `DefaultAgentBlock` class → **Register → Register New Step**, with:

| Field | Value |
| --- | --- |
| Message | `Create` |
| Primary Entity | `bot` |
| Secondary Entity | none |
| Event Pipeline Stage | **PreValidation** (recommended) or PreOperation |
| Execution Mode | **Synchronous** |
| Deployment | Server |
| Unsecure Configuration | the team / group GUID from step 2 |
| Secure Configuration | leave empty |

PreValidation runs before the database transaction opens, so the block costs nothing when it
fires and the maker gets the error immediately.

The unsecure configuration is passed to the plug-in **constructor**, and Dataverse caches plug-in
instances. After changing the GUID, update the step and expect the new value to take effect once
the cached instance is recycled — force it by disabling and re-enabling the step if needed.

### 4. Verify

* Sign in as a user **outside** the team and try to create an agent in Copilot Studio → the
  create fails with *"Agent creation is not allowed in the &lt;environment&gt; environment."*
* Sign in as a **member** of the team → the agent is created normally.
* Inspect *Settings → Plug-in trace log* in the environment for the `tracing.Trace` output if the
  outcome is not what you expect. Trace logs require *Plug-in and custom workflow activity
  tracing* to be set to **All** in the environment's system settings.

### 5. Package for other environments (ALM)

Register the assembly and step into an **unmanaged solution** so the whole configuration can be
promoted rather than re-registered by hand:

1. In PRT, use the solution picker when registering, or add the plug-in assembly and the SDK
   message processing step to a solution in the maker portal.
2. Export it as **managed** and import into each target environment:

```powershell
pac solution export --name AgentGovernance --managed --path .\AgentGovernance.zip
pac solution import --path .\AgentGovernance.zip --environment <target-env-url>
```

The exception-team GUID differs per environment, so parameterise the step's unsecure
configuration with an **environment variable** or override it in `deploymentSettings.json` at
import time — otherwise the imported step points at a team that does not exist in the target and
the plug-in fails closed, blocking *all* agent creation there.

### 6. Rolling back

Disable the step in PRT (right-click → *Disable*) to lift the block instantly without
uninstalling anything; delete the step or uninstall the managed solution to remove it entirely.

---

## Project details

* Target framework: .NET Framework 4.6.2 (required for sandboxed Dataverse plug-ins).
* Dependencies restored via `packages.config` — `Microsoft.CrmSdk.CoreAssemblies` 9.0.2.60 plus
  the `System.Text.Json` 8.0.5 chain used by the commented harness variants.
* `bin/`, `obj/`, `packages/`, `.vs/` and all key material (`*.pfx`, `*.snk`, `*.p12`) are
  git-ignored.
