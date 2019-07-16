using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.Monitor.Fluent.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
        public IConfigurationRoot Config { get; }

        public AzureSubscriptionCleaner(IAuthenticated authenticated, string subscriptionId, string exceptedRGsRegex, ILogger logger, IConfigurationRoot config)
        {
            this.Authenticated = authenticated;
            this.SubscriptionId = subscriptionId;
            this.ExceptedRGsRegex = exceptedRGsRegex;
            this.Logger = logger;
            this.Config = config;
        }

        internal void ProcessSubscription()
        {
            this.azure = this.Authenticated.WithSubscription(SubscriptionId);
            
            try
            {
                var rgs = azure.ResourceGroups.List().ToList();
                var cleanedRgs = new ConcurrentBag<IResourceGroup>();

                //foreach (var rg in rgs)
                Parallel.ForEach(rgs, rg =>
                {
                    try
                    {
                        if (!Regex.IsMatch(rg.Name, this.ExceptedRGsRegex))
                        {
                            if (ProcessResourceGroup(rg))
                            {
                                cleanedRgs.Add(rg);
                            }
                            else
                            {
                                Logger.LogError($"{rg.Name} is not deleted");
                            }
                        }
                        else
                        {
                            Logger.LogInformation($"{rg.Name} is exempted from cleanup");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(2, ex, $"Exception occured while processing Resource Group {rg.Name}");
                    }
                });

                foreach (var rg in cleanedRgs)
                {
                    Logger.LogInformation($"{rg.Name} is deleted");
                }

                Logger.LogInformation($"{cleanedRgs.Count} RGs are cleaned up in the subscription {SubscriptionId}");
            }
            catch (Exception ex)
            {
                Logger.LogError(1, ex, $"Exception occured while processing subscription {SubscriptionId}");
            }
        }

        private bool ProcessResourceGroup(IResourceGroup rg)
        {
            if (SetExpiryIfNotExists(rg))
            {
                return DeleteResourceGroupIfExpired(rg);
            }
            else
            {
                Logger.LogInformation(rg.Name);
                return false;
            }
        }

        private bool DeleteResourceGroupIfExpired(IResourceGroup rg)
        {
            UpdateExpiryIfRecentlyModified(rg);

            (var isExpiryValid, var expiry) = GetExpiry(rg.Tags);
            var now = DateTime.UtcNow;

            if (isExpiryValid && expiry.CompareTo(now) < 0)
            {
                if (Config["CleanupEnabled"]?.Equals(
                    Boolean.TrueString, StringComparison.CurrentCultureIgnoreCase) == true)
                {
                    // Delete the Resource Group
                    Logger.LogError($"RG {rg.Name} is being deleted");
                    azure.ResourceGroups.DeleteByName(rg.Name);
                    return true;
                }
            }

            return false;
        }

        private void UpdateExpiryIfRecentlyModified(IResourceGroup rg)
        {
            var log = GetLatestModifiedRecord(rg);
            if (log != null)
            {
                var modifiedBy = log.Inner.Caller.Substring(0, log.Inner.Caller.IndexOf('@'));
                UpdateExpiryAndOwner(rg, (DateTime)log.EventTimestamp, modifiedBy);
            }
            else
            {
                Logger.LogInformation($"Resource Group: {rg.Name} is not modified recently");
            }
        }

        private bool SetExpiryIfNotExists(IResourceGroup rg)
        {
            if (rg.Tags != null)
            {
                if (rg.Tags.ContainsKey("ManualSetup") || rg.Tags.ContainsKey("LongHaul"))
                {
                    return false;
                }

                if (rg.Tags.ContainsKey(expiresBy))
                {
                    (var isExpiryValidDateTime, var _) = GetExpiry(rg.Tags);
                    if (isExpiryValidDateTime)
                    {
                        Logger.LogInformation($"{rg.Name}: {rg.Tags[expiresBy]}");
                        return true;
                    }
                }
            }

            SetDefaultExpiry(rg, DateTime.UtcNow);
            return false;
        }

        private void UpdateExpiryAndOwner(IResourceGroup rg, DateTime then, string modifier)
        {
            var tags = new Dictionary<string, string>(rg.Tags);

            var updateRequired = false;

            var newExpiry = then.AddDays(8).Date;
            var (isExpiryValid, expiry) = GetExpiry(rg.Tags);

            if (!isExpiryValid || expiry.CompareTo(newExpiry) < 0)
            {
                tags[expiresBy] = newExpiry.ToString("o");
                updateRequired = true;
            }

            if (!rg.Tags.ContainsKey(lastModifiedBy) || !rg.Tags[lastModifiedBy].Equals(modifier))
            {
                tags[lastModifiedBy] = modifier;
                updateRequired = true;
            }

            if (updateRequired)
            {
                IResourceGroup updatedRg = rg.Update()
                     .WithTags(tags)
                     .Apply();
                Logger.LogInformation(
                    $"UpdateExpiryAndOwner RG:{updatedRg.Name}, LastModifiedBy: {updatedRg.Tags[lastModifiedBy]}, ExpiresBy: {updatedRg.Tags[expiresBy]}");
            }
        }


        private void SetDefaultExpiry(IResourceGroup rg, DateTime then)
        {
            string expiry = then.AddDays(8).Date.ToString("o");
            var tags = new Dictionary<string, string>(rg.Tags)
            {
                [expiresBy] = expiry
            };

            IResourceGroup updated = rg.Update()
                .WithTags(tags)
                .Apply();
            Logger.LogInformation($"rg:{rg.Name}, ExpiresBy: {expiry}");
        }

        private IEventData GetLatestModifiedRecord(IResourceGroup rg)
        {
            IEnumerable<IEventData> logs = azure.ActivityLogs
                .DefineQuery()
                .StartingFrom(DateTime.UtcNow.Subtract(TimeSpan.FromDays(1)))
                .EndsBefore(DateTime.UtcNow)
                .WithAllPropertiesInResponse()
                .FilterByResourceGroup(rg.Name)
                .Execute();

            var filteredLogs = logs.Where(x =>
                x.Category.Value.Equals("Administrative") &&
                x.Inner.Caller.Contains("@") && x.EventTimestamp != null)
                .OrderByDescending(x => x.EventTimestamp);

            if (filteredLogs.Any())
            {
                var log = filteredLogs.First();
                //Logger.LogInformation(JsonConvert.SerializeObject(log));
                return log;
            }
            else
            {
                return null;
            }
        }

        private (bool, DateTime) GetExpiry(IReadOnlyDictionary<string, string> tags)
        {
            if (!tags.ContainsKey(expiresBy))
            {
                return (false, default(DateTime));
            }

            var expiry = tags[expiresBy];
            DateTime expiryDate;
            return (DateTime.TryParse(expiry, out expiryDate), expiryDate);
        }

        private const string expiresBy = "ExpiresBy";
        private const string lastModifiedBy = "LastModifiedBy";

    }
}
