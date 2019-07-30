using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace AzureRgCleanup
{
    public static class MainFunction
    {
        [FunctionName("MainFunction")]
        public static void Run([TimerTrigger("0 0 */6 * * *", RunOnStartup = true)]TimerInfo myTimer, ILogger logger, ExecutionContext context)
        {
            logger.LogInformation($"C# Timer trigger function starting at: {DateTime.Now}");

            IConfigurationRoot config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            var isEnabled = config["IsEnabled"];
            if (isEnabled != null && isEnabled.Equals(Boolean.FalseString))
            {
                logger.LogInformation("Function not enabled");
                return;
            }

            var tenantId = config["TenantId"];
            var exceptedRGsRegex = config["Exceptions"];
            var environment = AzureEnvironment.AzureGlobalCloud;
            AzureCredentials credentials;
            var isRunningLocally = config["IsRunningLocally"];

            if (isRunningLocally != null && isRunningLocally.Equals(Boolean.TrueString))
            {
                var clientId = config["ClientId"];
                var clientSecret = config["ClientSecret"];
                credentials = SdkContext.AzureCredentialsFactory.FromServicePrincipal(clientId, clientSecret, tenantId, environment);
            }
            else
            {
                credentials = SdkContext.AzureCredentialsFactory.FromMSI(new MSILoginInformation(MSIResourceType.AppService), AzureEnvironment.AzureGlobalCloud);
            }

            var authenticated = Azure
                                .Configure()
                                .WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic)
                                .Authenticate(credentials);

            foreach (var subscriptionId in config["Subscriptions"].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var subCleaner = new AzureSubscriptionCleaner(authenticated, subscriptionId, exceptedRGsRegex, logger, config);
                    using (logger.BeginScope(
                        new Dictionary<string, object>
                        {
                            [nameof(subscriptionId)] = subscriptionId
                        })
                    )
                    {
                        subCleaner.ProcessSubscription();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        $"Exception occured while processing the subscription: {Environment.NewLine} {ex}");
                }
            }

            logger.LogInformation($"C# Timer trigger function finishing at: {DateTime.Now}");
        }

    }
}
