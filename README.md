# DefaultAgentBlock

A Dataverse plug-in that blocks the creation of Copilot Studio agents (`bot` records) in an
environment, with an exception for members of a configured team.

## How it works

The plug-in is registered on the **Create** message of the `bot` table. On every create it:

1. Reads a single GUID from the step's **unsecure configuration**. The GUID may be either a
   Dataverse `teamid` or the Entra ID (AAD) object ID of the group backing a group team.
2. Resolves the matching team(s) under the SYSTEM account, so the check does not depend on the
   calling user's privileges on `team` / `teammembership`.
3. Allows the create if either the initiating user or the impersonated user
   (`context.UserId`) is a member of one of those teams.
4. Otherwise throws an `InvalidPluginExecutionException` naming the current environment.

The create is also blocked when the configuration is missing or invalid, or when no matching
team exists in the environment — the block must never fail open.

`DefaultAgentBlock.cs` additionally contains two commented-out variants that block only
GitHub Copilot Harness agents (identified by the `CLICopilotRecognizer` recognizer kind in the
bot's `configuration` JSON) — one unconditional, one with the same team exception.

## Registration

Register with the Plug-in Registration Tool:

| Setting | Value |
| --- | --- |
| Message | `Create` |
| Primary entity | `bot` |
| Stage | Pre-validation or Pre-operation |
| Execution mode | Synchronous |
| Unsecure configuration | Team ID or AAD group object ID (GUID) |

## Building

Targets .NET Framework 4.6.2 and restores dependencies via `packages.config`
(Microsoft.CrmSdk.CoreAssemblies 9.0.2.60).

The project has `SignAssembly` enabled and references `LukasOplt.pfx`, which is **not** part of
this repository — signing keys must not be published. To build, supply your own key and point
`AssemblyOriginatorKeyFile` in `DefaultAgentBlock.csproj` at it, or set `SignAssembly` to
`false` (note that Dataverse requires plug-in assemblies to be strong-named).
