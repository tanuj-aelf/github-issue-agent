using Aevatar.Core.Abstractions;
using Aevatar.Extensions;
using AevatarTemplate.GAgents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateDefaultBuilder(args)
    .UseOrleansClient(client =>
    {
        client.UseLocalhostClustering()
            .UseMongoDBClient("mongodb://localhost:27017/?maxPoolSize=555")
            .AddMemoryStreams(AevatarCoreConstants.StreamProvider)
            .UseAevatar();
    })
    .ConfigureLogging(logging => logging.AddConsole())
    .UseConsoleLifetime();

using var host = builder.Build();
await host.StartAsync();

var gAgentFactory = host.Services.GetRequiredService<IGAgentFactory>();
var gAgentManager = host.Services.GetRequiredService<IGAgentManager>();

Console.WriteLine("Select an option:");
Console.WriteLine("1. List all available GAgents");
Console.WriteLine("2. Print description of SampleGAgent");

var choice = Console.ReadLine();

switch (choice)
{
    case "1":
        await ListAllAvailableGAgentsAsync(gAgentManager);
        break;
    case "2":
        await PrintDescriptionOfSampleGAgentAsync(gAgentFactory);
        break;
    default:
        Console.WriteLine("Invalid choice.");
        break;
}

async Task ListAllAvailableGAgentsAsync(IGAgentManager manager)
{
    var gAgents = manager.GetAvailableGAgentGrainTypes();
    foreach (var gAgent in gAgents)
    {
        Console.WriteLine(gAgent.ToString());
    }
}

async Task PrintDescriptionOfSampleGAgentAsync(IGAgentFactory factory)
{
    var sampleGAgent = await factory.GetGAgentAsync<IStateGAgent<SampleGAgentState>>();
    var description = await sampleGAgent.GetDescriptionAsync();
    Console.WriteLine(description);
}
