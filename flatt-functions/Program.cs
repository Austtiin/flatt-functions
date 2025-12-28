using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        
        // Add configuration
        services.AddSingleton<IConfiguration>(context.Configuration);
        
        // Add HttpClient for Azure API calls
        services.AddHttpClient();

        // Dev notice: Disable queue processing in dev
        var cfg = context.Configuration;
        var disabledFn = cfg["AzureWebJobs.ProcessInquiryQueue.Disabled"];
        var disableEmailService = cfg["DisableEmailService"] ?? Environment.GetEnvironmentVariable("DisableEmailService");
        var disableBlobService = cfg["DisableBlobService"] ?? Environment.GetEnvironmentVariable("DisableBlobService");
        if (string.Equals(disabledFn, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(disableEmailService, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(disableEmailService, "1", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("=== STORAGE ACCOUNT QUEUE RENEW NOT ACTIVE IN DEV ===");
        }

        if (string.Equals(disableBlobService, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(disableBlobService, "1", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("=== BLOB STORAGE CALLS DISABLED IN DEV ===");
        }
    })
    .Build();

host.Run();