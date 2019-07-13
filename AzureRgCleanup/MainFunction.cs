using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;

namespace AzureRgCleanup
{
    public static class MainFunction
    {
        [FunctionName("MainFunction")]
        public static void Run([TimerTrigger("0 */1 * * * *")]TimerInfo myTimer, ILogger logger, ExecutionContext context)
        //public static void Run([TimerTrigger("0 0 */1 * * *")]TimerInfo myTimer, ILogger log, ExecutionContext context)
        {
            logger.LogInformation($"C# Timer trigger function starting at: {DateTime.Now}");
            //var clientId = "4addd1e5-911d-4d43-9fcd-619989d96d83";

            var config = new ConfigurationBuilder()
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

            var clientId = config["ClientId"];
            var clientSecret = config["ClientSecret"];
            var tenantId = config["TenantId"];
            var exceptedRGsRegex = config["Exceptions"];
            var environment = AzureEnvironment.AzureGlobalCloud;
            AzureCredentials credentials;
            var isRunningLocally = config["IsRunningLocally"];

            if (isRunningLocally != null && isRunningLocally.Equals(Boolean.TrueString))
            {
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
                var subCleaner = new AzureSubscriptionCleaner(authenticated, subscriptionId, exceptedRGsRegex, logger);
                subCleaner.ProcessSubscription();
            }

            logger.LogInformation($"C# Timer trigger function finishing at: {DateTime.Now}");
        }

    }
}
