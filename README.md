# AzureResourceCleanup
Did you ever want your azure resources to be cleaned up automatically after use? Then AzureResourceCleanup is for you!

## How does the resource management work?
- The cleanup works at the level of resource group, not for each resource. Meaning, a Resource Group is deleted if and only if all the resources in the Resource Group are not being used or marked for non-deletion.

- Once the Azure function is deployed and configured with the required subscriptions, all the Resource Groups in the subscription are given a tag ExpiresBy, with Expiry Date(in UTC) based on the current date. The default expiry is configurable. 
