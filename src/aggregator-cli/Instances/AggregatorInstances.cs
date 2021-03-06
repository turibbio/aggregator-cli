﻿using Microsoft.Azure.Management.AppService.Fluent;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace aggregator.cli
{
    class AggregatorInstances
    {
        private readonly IAzure azure;
        private readonly ILogger logger;

        public AggregatorInstances(IAzure azure, ILogger logger)
        {
            this.azure = azure;
            this.logger = logger;
        }

        public async Task<IEnumerable<(string name, string region)>> ListAllAsync()
        {
            var rgs = await azure.ResourceGroups.ListAsync();
            var filter = rgs
                .Where(rg => rg.Name.StartsWith(InstanceName.ResourceGroupInstancePrefix));
            var result = new List<(string name, string region)>();
            foreach (var rg in filter)
            {
                result.Add((
                    InstanceName.FromResourceGroupName(rg.Name).PlainName,
                    rg.RegionName)
                );
            }
            return result;
        }

        public async Task<IEnumerable<string>> ListByLocationAsync(string location)
        {
            var rgs = await azure.ResourceGroups.ListAsync();
            var filter = rgs.Where(rg =>
                    rg.Name.StartsWith(InstanceName.ResourceGroupInstancePrefix)
                    && rg.RegionName.CompareTo(location) == 0);
            var result = new List<string>();
            foreach (var rg in filter)
            {
                result.Add(
                    InstanceName.FromResourceGroupName(rg.Name).PlainName);
            }
            return result;
        }

        internal async Task<bool> Add(InstanceName instance, string location)
        {
            string rgName = instance.ResourceGroupName;
            if (!await azure.ResourceGroups.ContainAsync(rgName))
            {
                logger.WriteVerbose($"Creating resource group {rgName}");
                await azure.ResourceGroups
                    .Define(rgName)
                    .WithRegion(location)
                    .CreateAsync();
                logger.WriteInfo($"Resource group {rgName} created.");
            }

            // TODO the template should create a Storage account and/or a Key Vault
            var resourceName = "aggregator.cli.Instances.instance-template.json";
            string armTemplateString;
            var assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (StreamReader reader = new StreamReader(stream))
            {
                armTemplateString = await reader.ReadToEndAsync();
            }

            var parsedTemplate = JObject.Parse(armTemplateString);
            // sanity checks
            if (parsedTemplate.SelectToken("parameters.appName") == null)
            {
                // not good, blah
                logger.WriteWarning($"Something is wrong with the ARM template");
            }

            string appName = instance.FunctionAppName;
            var templateParams = new Dictionary<string, Dictionary<string, object>>{
                    {"appName", new Dictionary<string, object>{{"value", appName } }}
            };

            string deploymentName = SdkContext.RandomResourceName("aggregator", 24);
            logger.WriteInfo($"Started deployment {deploymentName}");
            var deployment = await azure.Deployments.Define(deploymentName)
                    .WithExistingResourceGroup(rgName)
                    .WithTemplate(armTemplateString)
                    .WithParameters(templateParams)
                    .WithMode(DeploymentMode.Incremental)
                    .CreateAsync();

            // poll
            const int PollIntervalInSeconds = 3;
            int totalDelay = 0;
            while (!(StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Succeeded") ||
                    StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Failed") ||
                    StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Cancelled")))
            {
                SdkContext.DelayProvider.Delay(PollIntervalInSeconds * 1000);
                totalDelay += PollIntervalInSeconds;
                logger.WriteVerbose($"Deployment running ({totalDelay}s)");
                await deployment.RefreshAsync();
            }
            logger.WriteInfo($"Deployment {deployment.ProvisioningState}");

            // check runtime package
            logger.WriteVerbose($"Checking runtime package version");
            var package = new FunctionRuntimePackage();
            string zipPath = package.RuntimePackageFile;
            (string rel_name, DateTimeOffset? rel_when, string rel_url) = await package.FindVersion();
            logger.WriteVerbose($"Downloading runtime package {rel_name}");
            await package.Download(rel_url);
            logger.WriteInfo($"Runtime package downloaded.");

            // upload
            logger.WriteVerbose($"Uploading runtime package to {instance.DnsHostName}");
            bool ok = await package.UploadRuntimeZip(instance, azure, logger);
            if (ok)
            {
                logger.WriteInfo($"Runtime package uploaded to {instance.PlainName}.");
                // TODO requires VSTS logon!!!!!!!!!
                var vstsLogonData = VstsLogon.Load().connection;
                if (vstsLogonData.Mode == VstsTokenType.PAT)
                {
                    logger.WriteVerbose($"Saving VSTS token");
                    ok = await ChangeAppSettings(instance, vstsLogonData);
                    logger.WriteInfo($"VSTS token saved");
                }
                else
                {
                    logger.WriteWarning($"VSTS token type {vstsLogonData.Mode} is unsupported");
                    ok = false;
                }
            }
            return ok;
        }

        internal async Task<bool> ChangeAppSettings(InstanceName instance, VstsLogon vstsLogonData)
        {
            var webFunctionApp = await azure
                .AppServices
                .WebApps
                .GetByResourceGroupAsync(
                    instance.ResourceGroupName,
                    instance.FunctionAppName);
            var configuration = new AggregatorConfiguration();
            configuration.VstsTokenType = vstsLogonData.Mode;
            configuration.VstsToken = vstsLogonData.Token;
            configuration.Write(webFunctionApp);
            return true;
        }

        internal async Task<bool> Remove(InstanceName instance, string location)
        {
            string rgName = instance.ResourceGroupName;
            logger.WriteVerbose($"Searching instance {instance.PlainName}...");
            if (await azure.ResourceGroups.ContainAsync(rgName))
            {
                logger.WriteVerbose($"Deleting resource group {rgName}");
                await azure.ResourceGroups.DeleteByNameAsync(rgName);
                logger.WriteInfo($"Resource group {rgName} deleted.");
            }
            else
            {
                logger.WriteWarning($"Instance {instance.PlainName} not found in {location}.");
            }
            return true;
        }

        internal async Task<bool> SetAuthentication(InstanceName instance, string location)
        {
            bool ok;
            var vstsLogonData = VstsLogon.Load().connection;
            if (vstsLogonData.Mode == VstsTokenType.PAT)
            {
                logger.WriteVerbose($"Saving VSTS token");
                ok = await ChangeAppSettings(instance, vstsLogonData);
                logger.WriteInfo($"VSTS token saved");
            }
            else
            {
                logger.WriteWarning($"VSTS token type {vstsLogonData.Mode} is unsupported");
                ok = false;
            }
            return ok;
        }

    }
}
