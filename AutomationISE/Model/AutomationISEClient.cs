﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AutomationISE.Model
{
    using AzureAutomation;
    using AutomationISE.Model;
    using Microsoft.Azure;
    using Microsoft.Azure.Management.Resources;
    using Microsoft.Azure.Management.Resources.Models;
    using Microsoft.Azure.Management.Automation;
    using Microsoft.Azure.Management.Automation.Models;
    using Microsoft.Azure.Subscriptions;
    using Microsoft.Azure.Subscriptions.Models;
    using Microsoft.IdentityModel.Clients.ActiveDirectory;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using System.IO;
    using System.Threading;
    using System.Net.Http.Headers;

    public class AutomationISEClient
    {
        /* Azure Credential Data */
        public AuthenticationResult azureADAuthResult { get; set; }
        private TokenCloudCredentials cloudCredentials;
        private Microsoft.WindowsAzure.TokenCloudCredentials subscriptionCredentials;
        private SubscriptionCloudCredentials subscriptionCreds;

        /* Azure Clients */
        private ResourceManagementClient resourceManagementClient;
        private AutomationManagementClient automationManagementClient ;
        private Microsoft.WindowsAzure.Subscriptions.SubscriptionClient subscriptionClient;

        /* User Session Data */
        public Microsoft.WindowsAzure.Subscriptions.Models.SubscriptionListOperationResponse.Subscription currSubscription { get; set; }
        public AutomationAccount currAccount { get; set; }
        public String workspace { get; set; }

        private Dictionary<AutomationAccount, ResourceGroupExtended> accountResourceGroups;

        public AutomationISEClient()
        {
            /* Assign all members to null. They will only be instantiated when called upon */
            azureADAuthResult = null;
            cloudCredentials = null;
            subscriptionCreds = null;
            subscriptionClient = null;
            currSubscription = null;
            accountResourceGroups = null;
        }

        public async Task<IList<Microsoft.WindowsAzure.Subscriptions.Models.SubscriptionListOperationResponse.Subscription>> GetSubscriptions()
        {
            if (azureADAuthResult == null)
                throw new Exception("An Azure AD Authentication Result is needed to query Azure for subscriptions.");
            if (subscriptionClient == null)  //lazy instantiation
            {
                if (cloudCredentials == null)
                    subscriptionCredentials = new Microsoft.WindowsAzure.TokenCloudCredentials(azureADAuthResult.AccessToken);
                subscriptionClient = new Microsoft.WindowsAzure.Subscriptions.SubscriptionClient(subscriptionCredentials);
            }
            var cancelToken = new CancellationToken();
            Microsoft.WindowsAzure.Subscriptions.Models.SubscriptionListOperationResponse subscriptionsResult = await subscriptionClient.Subscriptions.ListAsync(cancelToken);
            return subscriptionsResult.Subscriptions;
        }

        public async Task<IList<AutomationAccount>> GetAutomationAccounts()
        {
            if(currSubscription == null)
                throw new Exception("Cannot get Automation Accounts until an Azure subscription has been set.");

            // Get the token for the tenant on this subscription.
            var cloudtoken = AuthenticateHelper.RefreshTokenByAuthority(currSubscription.ActiveDirectoryTenantId);
            subscriptionCreds = new TokenCloudCredentials(currSubscription.SubscriptionId, cloudtoken.AccessToken);

            automationManagementClient = new AutomationManagementClient(subscriptionCreds);
            
            // Add user agent string to indicate this is coming from the ISE automation client.
            ProductInfoHeaderValue ISEClientAgent = new ProductInfoHeaderValue(Constants.ISEUserAgent, Constants.ISEVersion);
            automationManagementClient.UserAgent.Add(ISEClientAgent);

            //TODO: does this belong here?
            if (accountResourceGroups == null)
                accountResourceGroups = new Dictionary<AutomationAccount, ResourceGroupExtended>();
            else
                accountResourceGroups.Clear();
            IList<AutomationAccount> result = new List<AutomationAccount>();
            IList<ResourceGroupExtended> resourceGroups = await this.GetResourceGroups();
            foreach (ResourceGroupExtended resourceGroup in resourceGroups)
            {
                AutomationAccountListResponse accountListResponse = await automationManagementClient.AutomationAccounts.ListAsync(resourceGroup.Name);
                foreach (AutomationAccount account in accountListResponse.AutomationAccounts)
                {
                    result.Add(account);
                    accountResourceGroups.Add(account, resourceGroup);
                }
            }
            return result;
        }

        public async Task<IList<ResourceGroupExtended>> GetResourceGroups()
        {
            if (currSubscription == null)
                throw new Exception("Cannot get Automation Accounts until an Azure subscription has been set.");

            // Get the token for the tenant on this subscription.
            var cloudtoken = AuthenticateHelper.RefreshTokenByAuthority(currSubscription.ActiveDirectoryTenantId);
            subscriptionCreds = new TokenCloudCredentials(currSubscription.SubscriptionId, cloudtoken.AccessToken);

            resourceManagementClient = new ResourceManagementClient(subscriptionCreds);

            ResourceGroupListResult resourceGroupResult = await resourceManagementClient.ResourceGroups.ListAsync(null);
            return resourceGroupResult.ResourceGroups;
        }

        private async Task<SortedSet<AutomationAsset>> GetAssetsInfo()
        {
            return (SortedSet<AutomationAsset>)await AutomationAssetManager.GetAll(getAccountWorkspace(), automationManagementClient, accountResourceGroups[currAccount].Name, currAccount.Name);
        }

        public async Task<SortedSet<AutomationAsset>> GetAssetsOfType(String type)
        {
            var assets = await GetAssetsInfo();

            var assetsOfType = new SortedSet<AutomationAsset>();
            foreach (var asset in assets)
            {
                if (asset.GetType().Name == type)
                {
                    assetsOfType.Add(asset);
                }
            }

            return assetsOfType;
        }

        public void DownloadAll()
        {
           AutomationAssetManager.DownloadAllFromCloud(getAccountWorkspace(), automationManagementClient, accountResourceGroups[currAccount].Name, currAccount.Name);
        }

        public void DownloadAssets(ICollection<AutomationAsset> assetsToDownload)
        {
            AutomationAssetManager.DownloadFromCloud(assetsToDownload, getAccountWorkspace(), automationManagementClient, accountResourceGroups[currAccount].Name, currAccount.Name);
        }

        public void UploadAssets(ICollection<AutomationAsset> assetsToUpload)
        {
            AutomationAssetManager.UploadToCloud(assetsToUpload, automationManagementClient, accountResourceGroups[currAccount].Name, currAccount.Name);

            // Since the cloud assets uploaded will have a last modified time of now, causing them to look newer than their local counterparts,
            // download the assets after upload to force last modified time between local and cloud to be the same, showing them as in sync (which they are)
            System.Threading.Thread.Sleep(1000); // TODO: seems to be a race condition. If we don't wait a bit first, the old asset values get downloaded instead of the just updated ones
            this.DownloadAssets(assetsToUpload);
        }

        public bool AccountWorkspaceExists()
        {
            return Directory.Exists(getAccountWorkspace());
        }

        private string getAccountWorkspace()
        {
            string accountPath = subscriptionCreds.SubscriptionId + "\\" + accountResourceGroups[currAccount].Name + "\\" + currAccount.Location + "\\" + currAccount.Name;
            return System.IO.Path.Combine(workspace, accountPath);
        }
    }
}