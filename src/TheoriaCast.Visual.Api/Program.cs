using System.Net;
using AIKernel.Abstractions.Hosting;
using AIKernel.Hosting;
using AIKernel.Providers.Distributed;
using TheoriaCast.Visual;
using TheoriaCast.Visual.Api;
using TheoriaCast.Visual.Mcp;

var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;
var data = Path.GetFullPath(config["Visual:Data"] ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheoriaCast", "Visual"));
var options = new DistributedHostOptions
{
    Service = VisualProtocol.Service,
    Name = config["Visual:ServerName"] ?? Environment.MachineName,
    BindAddress = new Uri(config["urls"] ?? config["Distributed:BindAddress"] ?? "http://127.0.0.1:39415"),
    AdvertisedAddress = config["Distributed:AdvertisedAddress"] is { } address ? new Uri(address) : null,
    PrivateDirectory = data,
    ConnectionFile = Path.GetFullPath(config["Visual:ConnectionFile"] ?? Path.Combine(data, "connection.json")),
    AllowPlainHttp = config.GetValue("Distributed:AllowPlainHttp", false),
    TrustedProxies = (config.GetSection("Distributed:TrustedProxies").Get<string[]>() ?? []).Select(IPAddress.Parse).ToArray()
};
builder.Services.AddAIKernelCore(config).WithDistributedApplication();
builder.Services.AddSingleton(_ => new VisualStore(data));
builder.Services.AddSingleton(_ => new VisualProcessing(config, data));
builder.Services.AddSingleton<VisualPeers>();
builder.Services.AddSingleton(sp => new VisualHost(options.Name, sp.GetRequiredService<ILogger<VisualLibrary>>()));
builder.Services.AddSingleton<VisualLibrary>();
builder.Services.AddSingleton<IVisualLibrary>(sp => sp.GetRequiredService<VisualLibrary>());
builder.Services.AddSingleton<KaraokeEncoder>();
builder.Services.AddSingleton(sp => new KaraokeLibrary(sp.GetRequiredService<VisualStore>(),
    sp.GetRequiredService<KaraokeEncoder>(), data, sp.GetRequiredService<ILogger<KaraokeLibrary>>()));
builder.Services.AddSingleton<IKaraokeLibrary>(sp => sp.GetRequiredService<KaraokeLibrary>());
builder.Services.AddSingleton<IApiEndpointProvider, VisualEndpoints>();
builder.Services.AddSingleton<IApiEndpointProvider, KaraokeEndpoints>();
builder.Services.AddSingleton<IMcpToolProvider, VisualTools>();
builder.Services.AddDistributedApplicationProvider(options);
builder.Services.AddHostedService<VisualQueue>();
builder.Services.AddHostedService<KaraokeWorker>();

using var host = builder.Build();
await host.RunAsync();
