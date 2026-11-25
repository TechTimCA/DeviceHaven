using DeviceHaven.Agent;
using DeviceHaven.Agent.Inventory;

// Command-line setup, used by the installer:
//   DeviceHaven.Agent.exe configure --server https://server:8443 --fingerprint AB:CD:.. --code XXXX-XXXX-XXXX-XXXX
if (args.Length > 0 && args[0].Equals("configure", StringComparison.OrdinalIgnoreCase))
{
    return Setup.Configure(args[1..], new AgentStore());
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "DeviceHaven Agent");
builder.Services.AddSingleton(new AgentStore());
builder.Services.AddSingleton(sp => InventoryCollectorFactory.Create(sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddHostedService(sp => new AgentWorker(
    sp.GetRequiredService<AgentStore>(),
    sp.GetRequiredService<IInventoryCollector>(),
    sp.GetRequiredService<ILogger<AgentWorker>>()));

builder.Build().Run();
return 0;
