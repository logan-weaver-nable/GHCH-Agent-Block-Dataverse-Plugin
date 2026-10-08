using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;

///////////////////////////////////////////////

// Block GHC copilot studio agents in environment


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
                "GitHub Copilot Harness agents cannot be created in this environment. Please reach out to the N-able IT team to request access, or use the Standard Harness."
            );
        }
    }
}