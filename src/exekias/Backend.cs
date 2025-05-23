﻿using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;
using Azure.ResourceManager.Batch;
using Azure.ResourceManager.Batch.Models;
using Azure.ResourceManager.CosmosDB;
using Azure.ResourceManager.CosmosDB.Models;
using Azure.ResourceManager.EventGrid;
using Azure.ResourceManager.EventGrid.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using Azure.Storage.Blobs.Specialized;
using System.Reflection;
using System.Text.Json.Nodes;
using Entra = Microsoft.Graph.Models;

partial class Worker
{
    public bool SubscriptionHasRequiredProviders(SubscriptionResource subscription)
    {
        var providers = subscription.GetResourceProviders(); //.GetAll();
        var requiredProviders = new[] { "Microsoft.Batch", "Microsoft.EventGrid", "Microsoft.Storage", "Microsoft.Web", "Microsoft.DocumentDB", "Microsoft.Insights", "Microsoft.OperationalInsights" };
        var tasks = Array.ConvertAll(requiredProviders, async p =>
        {
            try
            {
                ResourceProviderResource providerResource = await providers.GetAsync(p);
                if (providerResource.Data.RegistrationState == "Registered") return true;
                try
                {
                    providerResource = await providerResource.RegisterAsync();
                    return providerResource.Data.RegistrationState == "Registered";
                }
                catch (Azure.RequestFailedException ex)
                {
                    if (ex.Status == 403)
                    {
                        WriteError($"The provider {p} is not reqistered for subscription {subscription.Id.Name} and you do not have permissions to register it." +
                            " See https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/resource-providers-and-types#register-resource-provider for details.");
                        return false;
                    }
                    throw;
                }
            }
            catch (Azure.RequestFailedException ex)
            {
                if (ex.Status == 404)
                {
                    WriteError($"The provider {p} is not known for subscription {subscription.Id.Name}." +
                       " See https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/resource-providers-and-types#register-resource-provider for details.");
                    return false;
                }
                throw;
            }
        });
        Task.WaitAll(tasks);
        bool result = tasks.All(t => t.Result);
        return result;
    }

    public void DeployComponents(
        ResourceGroupResource resourceGroup,
        StorageAccountResource runStore,
        string containerName,
        string deploymentName,
        string metadataFilePattern)
    {
        //
        // Assumes the directory contains packages 'sync.zip', 'fetch.zip' and 'tables.zip'.
        // The packages are created by running 'dotnet publish' in the corresponding project
        // and then zipping the contents of the 'bin/Debug/net6.0/publish' directory.
        // See `exekiascmd.ps1` script.
        //
        var basePath = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? throw new InvalidOperationException("Cannot determine application base path");
        var templatePath = Path.Combine(basePath, "main.json");
        var syncPackagePath = Path.Combine(basePath, "Exekias.AzureFunctions.zip");
        var tablesPath = Path.Combine(basePath, "Exekias.DataImport.zip");

        var dontExist = string.Join(", ",
            (new[] { templatePath, syncPackagePath, tablesPath }).Where(p => !File.Exists(p)));
        if (dontExist.Length > 0) { throw new InvalidOperationException($"Cannot find the following file(s): {dontExist}"); }

        // Ensure run storage doesn't allow public access and shared keys
        runStore.Update(new StorageAccountPatch()
        {
            AllowBlobPublicAccess = false,
            AllowSharedKeyAccess = false
        });
        // Get the principal id of the current user
        var token = Credential.GetToken(new TokenRequestContext(["https://management.azure.com/.default"]), CancellationToken.None);
        string base64Payload = token.Token.Split('.')[1];
        var paddingLength = (4 - base64Payload.Length % 4) % 4;
        var jsonPayload = Convert.FromBase64String(base64Payload + new string('=', paddingLength));
        var tokenClaims = System.Text.Json.JsonDocument.Parse(jsonPayload).RootElement;
        var principalId = tokenClaims.GetProperty("oid").GetGuid();
        var principalName = "";
        try
        {
            principalName = tokenClaims.GetProperty("upn").GetString();
        }
        catch (Exception) { }
        WriteLine($"Using credential for {principalName} {principalId}");

        // Deploy ARM resources using template file
        ArmDeploymentResource deployment;
        try
        {
            deployment = resourceGroup.GetArmDeployments().CreateOrUpdate(Azure.WaitUntil.Completed,
                deploymentName,
                new ArmDeploymentContent(
                    new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
                    {
                        Template = BinaryData.FromStream(File.OpenRead(templatePath)),
                        Parameters = BinaryData.FromObjectAsJson(new JsonObject() {
                        {"location", new JsonObject(){ { "value", runStore.Data.Location.Name } } },
                        {"runStoreName", new JsonObject(){ {"value", runStore.Data.Name } } },
                        {"storeContainer", new JsonObject(){ {"value", containerName } } },
                        {"metadataFilePattern", new JsonObject(){ {"value", metadataFilePattern } } },
                        {"deploymentPrincipalId", new JsonObject(){ {"value", principalId.ToString() } } }
                        })
                    })).Value;
        }
        catch (Azure.RequestFailedException err)
        {
            throw new InvalidOperationException($"Deployment failed: {err.Message}");
        }
        WriteLine("The following resource have been created or updated:");
        foreach (var subResource in deployment.Data.Properties.OutputResources)
        {
            if (subResource is not null)
            {
                WriteLine($"  {subResource.Id}");
            }
        }
        var deploymentOutput = deployment.Data.Properties.Outputs.ToObjectFromJson<JsonObject>() ?? throw new InvalidOperationException("Cannot parse deployment outputs.");
        var syncFunctionId = deploymentOutput["syncFunctionId"]?["value"]?.GetValue<string?>();
        var batchAccountId = deploymentOutput["batchAccountId"]?["value"]?.GetValue<string?>();
        var batchPoolId = deploymentOutput["batchPoolId"]?["value"]?.GetValue<string?>();
        var metaStoreId = deploymentOutput["metaStoreId"]?["value"]?.GetValue<string?>();

        // There could only be single SystemTopic per storage account. Check if one has already been deployed.
        var systemTopicResources = GetResources($"resources | where type =~ 'microsoft.eventgrid/systemtopics' and properties.source =~ '{runStore.Id}'");
        SystemTopicResource topic = systemTopicResources.TotalRecords == 1
            ? Arm.GetSystemTopicResource(new ResourceIdentifier(JsonNode.Parse(systemTopicResources.Data)?[0]?["id"]?.GetValue<string>() ?? throw new NullReferenceException()))
            : resourceGroup.GetSystemTopics().CreateOrUpdate(Azure.WaitUntil.Completed, runStore.Data.Name + "8sync", new SystemTopicData(runStore.Data.Location.Name)
            {
                TopicType = "Microsoft.Storage.StorageAccounts",
                Source = runStore.Id
            }).Value;

        // authorize the user to access the backend services
        CosmosDBAccountResource metaStore = Arm.GetCosmosDBAccountResource(new ResourceIdentifier(metaStoreId!));
        WebSiteResource syncFunction = Arm.GetWebSiteResource(new ResourceIdentifier(syncFunctionId!)).Get();
        BatchAccountResource batchAccount = Arm.GetBatchAccountResource(new ResourceIdentifier(batchAccountId!));
        AuthorizeCredentials(runStore, topic, metaStore, syncFunction, batchAccount, principalId);

        // deploy syncFunction code from sync.zip
        var runChangeEventSink = "RunChangeEventSink";
        // the deployment created a new function app. Deploy code from a package.
        syncFunction = DeployFunctionCode(syncFunction, syncPackagePath, runChangeEventSink);
        WriteLine($"Deployed package {Path.GetFileName(syncPackagePath)} to Function app {syncFunction.Id.Name}");

        // subscribe sync function app to the storage eventgrid events
        WriteLine("Subscribing backend to storage events.");
        SiteFunctionResource update = syncFunction.GetSiteFunctions().Get(runChangeEventSink);
        var subscriptions = topic.GetSystemTopicEventSubscriptions();
        subscriptions.CreateOrUpdate(Azure.WaitUntil.Completed, syncFunction.Data.Name, new EventGridSubscriptionData()
        {
            Destination = new AzureFunctionEventSubscriptionDestination()
            {
                ResourceId = update.Id
            }
        });

        WriteLine("Adding application package to batch account.");
        PoolAssignApplication(batchPoolId!,
            UploadBatchApplicationPackage(tablesPath, batchAccountId!, "dataimport", "1.0.0"));

        // store the configuration in the run store hidden tag
        var patch = new StorageAccountPatch();
        patch.Tags[$"hidden-exekias-pattern-{containerName}"] = metadataFilePattern;
        patch.Tags[$"hidden-exekias-cosmos-{containerName}"] = $"{metaStore.Get().Value.Data.DocumentEndpoint}Exekias/{containerName}";
        runStore.Update(patch);
    }

    WebSiteResource DeployFunctionCode(WebSiteResource funcApp, string zipPath, string expected)
    {
        var packageUrl = funcApp.GetApplicationSettings().Value.Properties["WEBSITE_RUN_FROM_PACKAGE"] ?? throw new NullReferenceException("WEBSITE_RUN_FROM_PACKAGE property not set.");
        var blob = new Azure.Storage.Blobs.BlobClient(new Uri(packageUrl), Credential);
        blob.Upload(zipPath, overwrite: true);
        funcApp.Restart(synchronous: true);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        SiteFunctionResource? update = null;
        do
        {
            Thread.Sleep(30000); // 30 sec
            update = funcApp.GetSiteFunctions().Get(expected);  // "RunChangeEventSink");
        } while (update == null && stopwatch.Elapsed < TimeSpan.FromMinutes(5));

        return funcApp.Get();
    }

    BatchApplicationPackageReference UploadBatchApplicationPackage(string packagePath, string batchAccountId, string appName, string version)
    {
        var batchAccount = Arm.GetBatchAccountResource(new ResourceIdentifier(batchAccountId!));
        BatchApplicationResource batchAccountApplication = batchAccount
            .GetBatchApplications()
            .CreateOrUpdate(Azure.WaitUntil.Completed, appName, new BatchApplicationData())
            .Value;
        var appPackage = batchAccountApplication
            .GetBatchApplicationPackages()
            .CreateOrUpdate(Azure.WaitUntil.Completed, version, new BatchApplicationPackageData())
            .Value;
        var blobClient = new BlockBlobClient(appPackage.Data.StorageUri);
        using var packageStream = File.OpenRead(packagePath);
        blobClient.Upload(packageStream);
        WriteLine($"Uploaded app package {Path.GetFileName(packagePath)} to Batch Service {batchAccount.Id.Name} as {appName} v.{version}");
        appPackage.Activate(new BatchApplicationPackageActivateContent("zip"));
        return new BatchApplicationPackageReference(batchAccountApplication.Id) { Version = version };
    }

    void PoolAssignApplication(string batchPoolId, BatchApplicationPackageReference appReference)
    {
        var batchPool = Arm.GetBatchAccountPoolResource(new ResourceIdentifier(batchPoolId!));
        batchPool.Update(new BatchAccountPoolData()
        {
            ApplicationPackages = { appReference }
        });
    }

    void Authorize(string resourceKind, ArmResource resource, string roleName, Guid principalId)
    {
        var readerRole = resource
            .GetAuthorizationRoleDefinitions()
            .GetAll()
            .FirstOrDefault(r => r.Data.RoleName == roleName)
            ?.Data ?? throw new InvalidOperationException($"Cannot find {roleName} role definition.");
        if (resource.GetRoleAssignments().GetAll().All(r => r.Data.PrincipalId != principalId || r.Data.RoleDefinitionId != readerRole.Id))
        {
            resource.GetRoleAssignments().CreateOrUpdate(Azure.WaitUntil.Completed, Guid.NewGuid().ToString(), new RoleAssignmentCreateOrUpdateContent(
                roleDefinitionId: readerRole.Id,
                principalId: principalId));
            WriteLine($"Authorized {principalId} to access {resourceKind} {resource.Id.Name}: {readerRole.Description}.");
        }
    }

    void AuthorizeCredentials(
        StorageAccountResource data,
        SystemTopicResource topic,
        CosmosDBAccountResource meta,
        WebSiteResource functionApp,
        BatchAccountResource batch,
        Guid principalId)
    {
        // to discover storage and linked web site through Azure resource graph
        Authorize("storage account", data, "Reader", principalId);
        Authorize("storage event topic", topic, "Reader", principalId);

        // to read/write data
        Authorize("storage account", data, "Storage Blob Data Contributor", principalId);

        // to read/hide runs
        var cosmosRole = "Cosmos DB Built-in Data Contributor";
        var cosmosSqlReaderRole = meta
            .GetCosmosDBSqlRoleDefinitions()
            .GetAll()
            .FirstOrDefault(r => r.Data.RoleName == cosmosRole)
            ?.Data ?? throw new InvalidOperationException($"Cannot find {cosmosRole} role definition.");
        if (meta.GetCosmosDBSqlRoleAssignments().GetAll().All(r => r.Data.PrincipalId != principalId || r.Data.RoleDefinitionId != cosmosSqlReaderRole.Id))
        {
            meta.GetCosmosDBSqlRoleAssignments().CreateOrUpdate(Azure.WaitUntil.Completed, Guid.NewGuid().ToString(), new CosmosDBSqlRoleAssignmentCreateOrUpdateContent()
            {
                RoleDefinitionId = cosmosSqlReaderRole.Id,
                PrincipalId = principalId,
                Scope = meta.Id
            });
            WriteLine($"Authorized {principalId} to access Cosmos DB account {meta.Id.Name} as {cosmosRole}.");
        }

        // to allow reading configuration parameters: metadata file pattern, cosmos link, batch link
        Authorize("function app", functionApp, "Website Contributor", principalId);

        // to allow custom batch jobs
        Authorize("batch account", batch, "Contributor", principalId);
        ResourceIdentifier msiId = batch.Get().Value.Data.AutoStorage.NodeIdentityResourceId;
        var msiResource = Arm.GetGenericResource(msiId);  // Managed identity
        // to allow users to assign the managed identity to a new pool
        Authorize("pool identity", msiResource, "Managed Identity Operator", principalId);
    }

    async Task<(Guid principalId, string principalName)> GetPrincipal(string principalId)
    {
        static (Guid principalId, string principalName) returnUser(Entra.User user) =>
            (new Guid(user.Id!), $"user {user.DisplayName} {user.Mail}");

        static (Guid principalId, string principalName) returnGroup(Entra.Group group) =>
            (new Guid(group.Id!), $"group {group.DisplayName}");

        static (Guid principalId, string principalName) returnServicePrincipal(Entra.ServicePrincipal sp) =>
            (new Guid(sp.Id!), $"{sp.ServicePrincipalType} {sp.DisplayName}");

        var graphClient = new Microsoft.Graph.GraphServiceClient(Credential);
        if (Guid.TryParse(principalId, out Guid guid))
        {
            var userOrGroup = await graphClient.DirectoryObjects[principalId].GetAsync();
            if (userOrGroup is Entra.User user)
            {
                return returnUser(user);
            }
            else if (userOrGroup is Entra.Group group)
            {
                return returnGroup(group);
            }
            else if (userOrGroup is Entra.ServicePrincipal sp)
            {
                return returnServicePrincipal(sp);
            }
        }
        else
        {
            var userTask1 = graphClient.Users.GetAsync(requestConfiguration =>
            {
                requestConfiguration.QueryParameters.Filter = $"mail eq '{principalId}'";
            });
            var userTask2 = graphClient.Users.GetAsync(requestConfiguration =>
            {
                requestConfiguration.QueryParameters.Filter = $"userPrincipalName eq '{principalId}'";
            });
            var groupTask = graphClient.Groups.GetAsync(requestConfiguration =>
            {
                requestConfiguration.QueryParameters.Filter = $"displayName eq '{principalId}'";
            });
            var spTask = graphClient.ServicePrincipals.GetAsync(requestConfiguration =>
            {
                requestConfiguration.QueryParameters.Filter = $"displayName eq '{principalId}'";
            });
            try
            {
                await Task.WhenAll(userTask1, userTask2, groupTask, spTask);
            }
            catch (Entra.ODataErrors.ODataError) { }
            if (userTask1.IsCompletedSuccessfully && userTask1.Result?.Value is not null && userTask1.Result.Value.Count == 1)
            {
                return returnUser(userTask1.Result.Value[0]);
            }
            if (userTask2.IsCompletedSuccessfully && userTask2.Result?.Value is not null && userTask2.Result.Value.Count == 1)
            {
                return returnUser(userTask2.Result.Value[0]);
            }
            if (groupTask.IsCompletedSuccessfully && groupTask.Result?.Value is not null && groupTask.Result.Value.Count == 1)
            {
                return returnGroup(groupTask.Result.Value[0]);
            }
            if (spTask.IsCompletedSuccessfully && spTask.Result?.Value is not null && spTask.Result.Value.Count == 1)
            {
                return returnServicePrincipal(spTask.Result.Value[0]);
            }
        }
        throw new InvalidOperationException($"Cannot find user or group with id or mail '{principalId}'.");
    }

    public const string METADATA_FILE_PATTERN = @"^(?<runId>(?<timestamp>(?<date>[\d]+)-(?<time>[\d]+))-(?<title>[^/]*))/params.json$";

    public int DoBackendDeploy(
        string? subscription,
        string? resourceGroupName,
        string? location,
        string? storageAccountName,
        string? blobContainerName,
        string? metadataFilePattern)
    {
        try
        {
            var runStoreResource = new Lazy<StorageAccountResource>(() =>
                Arm.GetStorageAccountResource(ResourceIdentifier.Parse(runStoreResourceId)).Get()
            );
            var runStoreSubscription = new Lazy<SubscriptionResource>(() =>
                Arm.GetSubscriptionResource(ResourceIdentifier.Parse(
                    $"/subscriptions/{runStoreResource.Value.Id.SubscriptionId}"
                )).Get()
            );

            bool needConfirmation = false;
            SubscriptionResource subscriptionResource;
            if (subscription is null && ConfigOrNone is not null)
            {
                subscriptionResource = runStoreSubscription.Value;
                WriteLine($"Using subscription {subscriptionResource.Data.DisplayName} from {ConfigFile?.FullName}");
                needConfirmation = true;
            }
            else
            {
                subscriptionResource = AskSubscription(subscription);
            }

            if (!SubscriptionHasRequiredProviders(subscriptionResource)) { return 1; }

            if (resourceGroupName is null && ConfigOrNone is not null)
            {
                resourceGroupName = runStoreResource.Value.Data.Id.ResourceGroupName;
                WriteLine($"Using resource group name {resourceGroupName} from {ConfigFile?.FullName}");
                needConfirmation = true;
            }
            var resourceGroup = AskResourceGroup(resourceGroupName, subscriptionResource, true, location);

            if (storageAccountName is null && ConfigOrNone is not null)
            {
                storageAccountName = runStoreResource.Value.Data.Name;
                WriteLine($"Using storage account name {storageAccountName} from {ConfigFile?.FullName}");
                needConfirmation = true;
            }
            var storageAccount = AskStorageAccount(storageAccountName, resourceGroup, true);
            if (blobContainerName is null)
            {
                if (ConfigOrNone is not null)
                {
                    blobContainerName = runStoreContainerName;
                    WriteLine($"Using blob container name {blobContainerName} from {ConfigFile?.FullName}");
                    needConfirmation = true;
                }
                else if (!storageAccount.GetBlobService().GetBlobContainers().Any())
                {
                    blobContainerName = "runs";
                }
                else
                {
                    blobContainerName = AskBlobContainerName(null, storageAccount);
                }
            }
            if (metadataFilePattern is null)
            {
                if (ConfigOrNone is not null)
                {
                    metadataFilePattern = ConfigOrNone.runStoreMetadataFilePattern;
                    WriteLine($"Using metadata file pattern {metadataFilePattern} from {ConfigFile?.FullName}");
                    needConfirmation = true;
                }
                else { metadataFilePattern = METADATA_FILE_PATTERN; }
            }
            var deploymentName = $"exekias_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            WriteLine($"Start deployment {deploymentName} of backend services for {storageAccount.Id.Name}/{blobContainerName} in {subscriptionResource.Data.DisplayName}.");
            if (needConfirmation && Choose("Proceed?", ["Yes", "No"], false) != 0)
            {
                return 1;
            }
            DeployComponents(resourceGroup, storageAccount, blobContainerName, deploymentName, metadataFilePattern);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }
    public int DoBackendBatchAppUpload(
        string? rid,  // temporary
        string? path,
        string? version)
    {
        try
        {
            UploadBatchApplicationPackage(path!, rid!, "dataimport", version!);
            return 0;
        }
        catch (Exception ex)
        {
            WriteError($"Error {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    public async Task<int> DoBackendAllow(string principalId)
    {
        if (ConfigDoesNotExist) { return 1; }
        try
        {
            (var principalGuid, var principalName) = await GetPrincipal(principalId);
            WriteLine($"Authorizing {principalName} to access backend services.");
            var runStoreRid = ResourceIdentifier.Parse(runStoreResourceId);
            var runStoreResourceTask = Arm.GetStorageAccountResource(runStoreRid).GetAsync();
            var exekiasStoreRid = ResourceIdentifier.Parse(exekiasStoreResourceId);
            var link = FindEventSubscription(runStoreRid, Config.runStoreUrl) ?? throw new ApplicationException();
            var topicRid = link.Id.Parent;
            var destination = (AzureFunctionEventSubscriptionDestination)link.Data.Destination;
            var functionRid = destination.ResourceId.Parent;
            var metaStoreResourceTask = Arm.GetCosmosDBAccountResource(exekiasStoreRid).GetAsync();
            // var topicRid = ResourceIdentifier.Parse(string.Format(
            //     "/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.EventGrid/systemTopics/{2}",
            //     runStoreRid.SubscriptionId,
            //     runStoreRid.ResourceGroupName,
            //     runStoreRid.Name));
            var topicResourceTask = Arm.GetSystemTopicResource(topicRid).GetAsync();
            // var functionRid = ResourceIdentifier.Parse(string.Format(
            //     "/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Web/sites/{2}-{3}",
            //     exekiasStoreRid.SubscriptionId,
            //     exekiasStoreRid.ResourceGroupName,
            //     exekiasStoreRid.Name,
            //     runStoreContainerName));
            var functionResourceTask = Arm.GetWebSiteResource(functionRid).GetAsync();
            var batchRid = ResourceIdentifier.Parse(string.Format(
                "/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Batch/batchAccounts/{2}",
                runStoreRid.SubscriptionId,
                runStoreRid.ResourceGroupName,
                runStoreRid.Name));
            var batchResourceTask = Arm.GetBatchAccountResource(batchRid).GetAsync();
            await Task.WhenAll(runStoreResourceTask, metaStoreResourceTask, topicResourceTask, functionResourceTask, batchResourceTask);
            AuthorizeCredentials(runStoreResourceTask.Result.Value, topicResourceTask.Result.Value, metaStoreResourceTask.Result.Value, functionResourceTask.Result.Value, batchResourceTask.Result.Value, principalGuid);
            return 0;
        }
        catch (Exception ex)
        {
            WriteError($"Error {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

}
