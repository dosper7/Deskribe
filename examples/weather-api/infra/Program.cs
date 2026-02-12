using Pulumi;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.DBforPostgreSQL;
using Pulumi.AzureNative.DBforPostgreSQL.Inputs;

return await Pulumi.Deployment.RunAsync(() =>
{
    var config = new Config();
    var appName = config.Get("appName") ?? "weather-api";
    var environment = config.Get("environment") ?? "prod";
    var region = config.Get("region") ?? "westeurope";

    var resourceGroupName = $"rg-{appName}-{environment}";
    var serverName = $"pg-{appName}-{environment}";
    var dbName = appName.Replace("-", "");

    // Resource Group
    var resourceGroup = new ResourceGroup(resourceGroupName, new ResourceGroupArgs
    {
        ResourceGroupName = resourceGroupName,
        Location = region
    });

    // Generate admin password
    var adminPassword = new Pulumi.Random.RandomPassword("pg-admin-password", new()
    {
        Length = 24,
        Special = true,
        OverrideSpecial = "!@#$%"
    });

    // PostgreSQL Flexible Server
    var pgServer = new Server(serverName, new ServerArgs
    {
        ServerName = serverName,
        ResourceGroupName = resourceGroup.Name,
        Location = resourceGroup.Location,
        Version = "16",
        Sku = new SkuArgs
        {
            Name = "Standard_B1ms",
            Tier = SkuTier.Burstable
        },
        Storage = new StorageArgs
        {
            StorageSizeGB = 32
        },
        AdministratorLogin = "pgadmin",
        AdministratorLoginPassword = adminPassword.Result,
        AuthConfig = new AuthConfigArgs
        {
            ActiveDirectoryAuth = ActiveDirectoryAuthEnum.Disabled,
            PasswordAuth = PasswordAuthEnum.Enabled
        }
    });

    // Database
    var database = new Database(dbName, new DatabaseArgs
    {
        DatabaseName = dbName,
        ServerName = pgServer.Name,
        ResourceGroupName = resourceGroup.Name,
        Charset = "UTF8",
        Collation = "en_US.utf8"
    });

    // Firewall rule: Allow Azure services
    var firewallRule = new FirewallRule("allow-azure-services", new FirewallRuleArgs
    {
        FirewallRuleName = "AllowAzureServices",
        ServerName = pgServer.Name,
        ResourceGroupName = resourceGroup.Name,
        StartIpAddress = "0.0.0.0",
        EndIpAddress = "0.0.0.0"
    });

    // Outputs
    var connectionString = Output.Format(
        $"Host={pgServer.FullyQualifiedDomainName};Port=5432;Database={dbName};Username=pgadmin;Password={adminPassword.Result};SSL Mode=Require");

    return new Dictionary<string, object?>
    {
        ["postgres.connectionString"] = connectionString,
        ["postgres.endpoint"] = pgServer.FullyQualifiedDomainName,
        ["resourceGroupName"] = resourceGroup.Name
    };
});
