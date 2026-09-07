using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;


namespace DefaultAgentBlock
{
    /// <summary>
    /// Blocks the creation of Copilot Studio agents (bot records) in the environment
    /// unless the calling user is a member of a configured team. The plugin is meant
    /// to be registered on the Create message of the bot table.
    /// </summary>
    public class DefaultAgentBlock : IPlugin
    {
        // Team that is allowed to create agents; null when the configuration is missing or invalid.
        private readonly Guid? allowedGroupId;

        /// <summary>
        /// The unsecure configuration must contain a single GUID — either the Dataverse
        /// team ID or the Entra ID (AAD) object ID of the group backing a group team.
        /// </summary>
        public DefaultAgentBlock(string unsecureConfiguration, string secureConfiguration)
        {
            Guid groupId;
            if (Guid.TryParse(unsecureConfiguration, out groupId))
            {
                allowedGroupId = groupId;
            }
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            // Fail fast when the plugin step is registered without a valid GUID —
            // otherwise the block could be silently bypassed.
            if (!allowedGroupId.HasValue)
            {
                tracing.Trace("The allowed team/group ID is missing or invalid in the unsecure configuration.");
                throw new InvalidPluginExecutionException(
                    "Agent creation is not configured correctly."
                );
            }

            var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(
                typeof(IOrganizationServiceFactory)
            );
            // Run the queries under the SYSTEM account so the check does not depend
            // on the calling user's read privileges on team/teammembership.
            var service = serviceFactory.CreateOrganizationService(null);

            // The configured GUID can be either a Dataverse team ID or the
            // Entra ID (AAD) object ID of the group backing a group team.
            var teams = service.RetrieveMultiple(
                new QueryExpression("team")
                {
                    ColumnSet = new ColumnSet("teamtype", "name"),
                    Criteria = new FilterExpression(LogicalOperator.Or)
                    {
                        Conditions =
                        {
                            new ConditionExpression("teamid", ConditionOperator.Equal, allowedGroupId.Value),
                            new ConditionExpression("azureactivedirectoryobjectid", ConditionOperator.Equal, allowedGroupId.Value)
                        }
                    }
                }
            );

            // Without an existing team the exception list cannot be evaluated — block the create.
            if (teams.Entities.Count == 0)
            {
                tracing.Trace(
                    "No team found with teamid or AAD object id {0}.",
                    allowedGroupId.Value
                );
                throw new InvalidPluginExecutionException(
                    "Agent creation exception team was not found in this environment."
                );
            }

            tracing.Trace(
                "InitiatingUserId: {0}, UserId: {1}",
                context.InitiatingUserId,
                context.UserId
            );

            // Allow the create as soon as either the initiating user or the impersonated
            // user (context.UserId) is a member of any of the matched teams.
            foreach (var team in teams.Entities)
            {
                var teamType = team.GetAttributeValue<OptionSetValue>("teamtype");
                tracing.Trace(
                    "Checking membership in team '{0}'. TeamId: {1}, TeamType: {2}",
                    team.GetAttributeValue<string>("name"),
                    team.Id,
                    teamType == null ? "missing" : teamType.Value.ToString()
                );

                var membership = service.RetrieveMultiple(
                    new QueryExpression("teammembership")
                    {
                        ColumnSet = new ColumnSet(false),
                        TopCount = 1,
                        Criteria = new FilterExpression(LogicalOperator.And)
                        {
                            Conditions =
                            {
                                new ConditionExpression("teamid", ConditionOperator.Equal, team.Id),
                                new ConditionExpression(
                                    "systemuserid",
                                    ConditionOperator.In,
                                    context.InitiatingUserId,
                                    context.UserId
                                )
                            }
                        }
                    }
                );

                if (membership.Entities.Count > 0)
                {
                    // User belongs to the allowed team — let the agent creation proceed.
                    tracing.Trace("Team membership found for the operation identity.");
                    return;
                }
            }

            tracing.Trace("Team membership not found for the operation identity.");

            // User is not in the allowed team — block the create with a message that
            // names the environment the plugin is running in.
            throw new InvalidPluginExecutionException(
                string.Format(
                    "Agent creation is not allowed in the {0} environment.",
                    GetEnvironmentName(service, tracing)
                )
            );
        }

        /// <summary>
        /// Returns the display (friendly) name of the current environment as shown in the
        /// Power Platform admin center, or "current" when it cannot be retrieved — the
        /// lookup must never turn the block message itself into a failure.
        /// </summary>
        private static string GetEnvironmentName(IOrganizationService service, ITracingService tracing)
        {
            try
            {
                var response = (RetrieveCurrentOrganizationResponse)service.Execute(
                    new RetrieveCurrentOrganizationRequest()
                );

                var friendlyName = response.Detail != null ? response.Detail.FriendlyName : null;
                if (!string.IsNullOrWhiteSpace(friendlyName))
                {
                    return friendlyName;
                }
            }
            catch (Exception exception)
            {
                tracing.Trace("Failed to retrieve the environment name: {0}", exception.Message);
            }

            return "current";
        }
    }
}

///////////////////////////////////////////////

// Block GHC copilot studio agents in environment

/*
using Microsoft.Xrm.Sdk;
using System;
using System.Text.Json;

namespace Plugins
{
    public class BlockCopilotHarnessAgents : IPlugin
    {
        private const string BotTableName = "bot";
        private const string ConfigurationColumnName = "configuration";
        private const string HarnessRecognizerKind = "CLICopilotRecognizer";

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            if (!context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase) ||
                !context.PrimaryEntityName.Equals(BotTableName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!context.InputParameters.Contains("Target") ||
                !(context.InputParameters["Target"] is Entity target))
            {
                return;
            }

            ValidateBot(target, tracing);
        }

        private static void ValidateBot(
            Entity target,
            ITracingService tracing)
        {
            string configuration = target.GetAttributeValue<string>(ConfigurationColumnName);
            if (string.IsNullOrWhiteSpace(configuration))
            {
                return;
            }

            try
            {
                using (JsonDocument configurationJson = JsonDocument.Parse(configuration))
                {
                    JsonElement root = configurationJson.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("recognizer", out JsonElement recognizer) ||
                        recognizer.ValueKind != JsonValueKind.Object ||
                        !recognizer.TryGetProperty("$kind", out JsonElement recognizerKind) ||
                        recognizerKind.ValueKind != JsonValueKind.String ||
                        !string.Equals(recognizerKind.GetString(), HarnessRecognizerKind, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }
            catch (JsonException exception)
            {
                tracing.Trace("The bot configuration is not valid JSON: {0}", exception.Message);
                return;
            }

            throw new InvalidPluginExecutionException(
                "GitHub Copilot Harness agents cannot be created in this environment."
            );
        }
    }
}
*/

///////////////////////////////////////////////

// Block GHC copilot studio agents in environment with exeption for specific group

/*
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Text.Json;

namespace Plugins
{
    public class BlockCopilotHarnessAgents : IPlugin
    {
        private const string BotTableName = "bot";
        private const string ConfigurationColumnName = "configuration";
        private const string HarnessRecognizerKind = "CLICopilotRecognizer";
        private readonly Guid? allowedTeamId;

        public BlockCopilotHarnessAgents(string unsecureConfiguration, string secureConfiguration)
        {
            Guid teamId;
            if (Guid.TryParse(unsecureConfiguration, out teamId))
            {
                allowedTeamId = teamId;
            }
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            if (!context.MessageName.Equals("Create", StringComparison.OrdinalIgnoreCase) ||
                !context.PrimaryEntityName.Equals(BotTableName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!context.InputParameters.Contains("Target") ||
                !(context.InputParameters["Target"] is Entity target))
            {
                return;
            }

            var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(
                typeof(IOrganizationServiceFactory)
            );

            ValidateBot(target, context, serviceFactory, tracing);
        }

        private void ValidateBot(
            Entity target,
            IPluginExecutionContext context,
            IOrganizationServiceFactory serviceFactory,
            ITracingService tracing)
        {
            string configuration = target.GetAttributeValue<string>(ConfigurationColumnName);
            if (string.IsNullOrWhiteSpace(configuration))
            {
                return;
            }

            try
            {
                using (JsonDocument configurationJson = JsonDocument.Parse(configuration))
                {
                    JsonElement root = configurationJson.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("recognizer", out JsonElement recognizer) ||
                        recognizer.ValueKind != JsonValueKind.Object ||
                        !recognizer.TryGetProperty("$kind", out JsonElement recognizerKind) ||
                        recognizerKind.ValueKind != JsonValueKind.String ||
                        !string.Equals(recognizerKind.GetString(), HarnessRecognizerKind, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }
            catch (JsonException exception)
            {
                tracing.Trace("The bot configuration is not valid JSON: {0}", exception.Message);
                return;
            }

            if (!allowedTeamId.HasValue)
            {
                tracing.Trace("The allowed team ID is missing or invalid in the unsecure configuration.");
                throw new InvalidPluginExecutionException(
                    "GitHub Copilot Harness agent creation is not configured correctly."
                );
            }

            var service = serviceFactory.CreateOrganizationService(null);
            var membership = service.RetrieveMultiple(
                new QueryExpression("teammembership")
                {
                    ColumnSet = new ColumnSet(false),
                    TopCount = 1,
                    Criteria = new FilterExpression(LogicalOperator.And)
                    {
                        Conditions =
                        {
                            new ConditionExpression("teamid", ConditionOperator.Equal, allowedTeamId.Value),
                            new ConditionExpression("systemuserid", ConditionOperator.Equal, context.InitiatingUserId)
                        }
                    }
                }
            );

            if (membership.Entities.Count > 0)
            {
                return;
            }

            throw new InvalidPluginExecutionException(
                "GitHub Copilot Harness agents cannot be created in this environment."
            );
        }
    }
}
*/

