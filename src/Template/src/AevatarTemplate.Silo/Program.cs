using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AevatarTemplate.Silo;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.secrets.json", optional: true)
            .Build();
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        try
        {
            Log.Information("Starting Silo");
            await CreateHostBuilder(args).RunConsoleAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host terminated unexpectedly!");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureServices((_, services) => { services.AddApplication<AevatarSiloModule>(); })
            .UseOrleansConfiguration()
            .UseAutofac()
            .UseSerilog();
}