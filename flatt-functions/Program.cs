#nullable enable
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using flatt_functions.Data;
using System;

namespace flatt_functions
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var host = new HostBuilder()
                .ConfigureFunctionsWorkerDefaults()
                .ConfigureFunctionsWebApplication() // <-- Add this line
                .ConfigureAppConfiguration(cfg =>
                {
                    cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                    cfg.AddEnvironmentVariables();
                })
                .ConfigureServices((context, services) =>
                {
                    var cs = context.Configuration["SqlConnectionString"];
                    services.AddDbContext<InventoryContext>(options => options.UseSqlServer(cs));
                    services.AddApplicationInsightsTelemetryWorkerService();
                    services.ConfigureFunctionsApplicationInsights();
                })
                .Build();

            host.Run();
        }
    }
}
