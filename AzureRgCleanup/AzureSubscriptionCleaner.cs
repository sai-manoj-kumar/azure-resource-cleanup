using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static Microsoft.Azure.Management.Fluent.Azure;

namespace AzureRgCleanup
{
    class AzureSubscriptionCleaner : IAzureSubscriptionCleaner
    {
        private IAzure azure;

        public IAuthenticated Authenticated { get; }
        public string SubscriptionId { get; }
        public string ExceptedRGsRegex { get; }
        public ILogger Logger { get; }

        public AzureSubscriptionCleaner(IAuthenticated authenticated, string subscriptionId, string exceptedRGsRegex, ILogger logger)
        {
            this.Authenticated = authenticated;
            this.SubscriptionId = subscriptionId;
            this.ExceptedRGsRegex = exceptedRGsRegex;
            this.Logger = logger;
        }

        internal void ProcessSubscription()
        {
            this.azure = this.Authenticated.WithSubscription(SubscriptionId);
            
            try
            {
                var rgs = azure.ResourceGroups.List().ToList();
                foreach (var rg in rgs)
                {
                    try
                    {
                        if (!Regex.IsMatch(rg.Name, this.ExceptedRGsRegex))
                        {
                            ProcessResourceGroup(rg, azure);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(2, ex, $"Exception occured while processing Resource Group {rg.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(1, ex, $"Exception occured while processing subscription {SubscriptionId}");
            }
        }

        private void ProcessResourceGroup(IResourceGroup rg, IAzure azure)
        {
            if (SetExpiryIfNotExists(rg))
            {
                DeleteResourceGroupIfExpired(azure, rg);
            }
        }

        private void DeleteResourceGroupIfExpired(IAzure azure, IResourceGroup rg)
        {
            (var _, var expiry) = GetExpiry(rg.Tags);
            var now = DateTime.UtcNow;

            if (expiry.CompareTo(now) < 0)
            {
                Logger.LogWarning($"RG {rg.Name} will be deleted");
            }
            else
            {
                IsModifiedAfter(azure, rg);
            }
        }

        private bool SetExpiryIfNotExists(IResourceGroup rg)
        {
            if (rg.Tags != null)
            {
                if (rg.Tags.ContainsKey("ExpiresBy"))
                {
                    (var isExpiryValidDateTime, var _) = GetExpiry(rg.Tags);
                    if (isExpiryValidDateTime)
                    {
                        Logger.LogInformation($"{rg.Name}: {rg.Tags["ExpiresBy"]}");
                        return true;
                    }
                }
            }

            SetExpiry(rg);
            return false;
        }

        private void SetExpiry(IResourceGroup rg)
        {
            var expiry = DateTime.UtcNow.AddDays(7).ToString("o");
            rg.Update()
                .WithTag("ExpiresBy", expiry)
                .Apply();
            Logger.LogInformation($"rg:{rg.Name}, ExpiresBy: {expiry}");
        }

        private bool IsModifiedAfter(IAzure azure, IResourceGroup rg)
        {
            var logs = azure.ActivityLogs
                .DefineQuery()
                .StartingFrom(DateTime.UtcNow.Subtract(TimeSpan.FromDays(1)))
                .EndsBefore(DateTime.UtcNow)
                .WithAllPropertiesInResponse()
                .FilterByResourceGroup(rg.Name)
                .Execute();

            foreach (var log in logs)
            {
                Logger.LogInformation(JsonConvert.SerializeObject(log));
            }
            

            return true;
        }

        private (bool, DateTime) GetExpiry(IReadOnlyDictionary<string, string> tags)
        {
            var expiry = tags["ExpiresBy"];
            DateTime expiryDate;
            return (DateTime.TryParse(expiry, out expiryDate), expiryDate);
        }


    }
}
