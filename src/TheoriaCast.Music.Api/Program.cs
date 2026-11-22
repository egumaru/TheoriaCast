using System.Net;
using AIKernel.Abstractions.Hosting;
using AIKernel.Hosting;
using AIKernel.Providers.Distributed;
using TheoriaCast.Music;
using TheoriaCast.Music.Api;
using TheoriaCast.Music.Mcp;

if (args.FirstOrDefault() == "worker")
{
    await MusicWorker.Run(args[1..]);
    return;
}

if (args.FirstOrDefault()=="worker-token")
{
    if (args.Length!=3 || !File.Exists(Path.Combine(args[1],"server-access.json")))
        throw new ArgumentException("worker-token <existing API data directory> <new private token file>");
    var secret=await new ApplicationCredentials(args[1]).GetSecretAsync("worker");
    var output=Path.GetFullPath(args[2]);
    var mode=new FileStreamOptions { Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None };
    if (!OperatingSystem.IsWindows()) mode.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
    using var stream=OperatingSystem.IsWindows()?CreatePrivateWindows(output):new FileStream(output,mode);
    using var writer=new StreamWriter(stream);
    writer.Write(secret);
    return;
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static FileStream CreatePrivateWindows(string path)
{
    using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
    var acl=new System.Security.AccessControl.FileSecurity();
    acl.SetAccessRuleProtection(true,false);
    acl.AddAccessRule(new(identity.User!,System.Security.AccessControl.FileSystemRights.FullControl,System.Security.AccessControl.AccessControlType.Allow));
    return new FileInfo(path).Create(FileMode.CreateNew,System.Security.AccessControl.FileSystemRights.FullControl,FileShare.None,4096,FileOptions.None,acl);
}

var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;
var data = Path.GetFullPath(config["Music:Data"] ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheoriaCast", "Music"));
var addresses=(config["urls"] ?? config["Distributed:BindAddress"] ?? config["ASPNETCORE_URLS"] ?? "http://127.0.0.1:39395")
    .Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(s=>new Uri(s)).Distinct().ToArray();
var bind = addresses.First();
var advertised = config["public-url"] ?? config["Distributed:AdvertisedAddress"] ?? config["Music:PublicUrl"];
var options = new DistributedHostOptions {
    Service = "TheoriaCast.Music.Api", Name = config["server-name"] ?? config["Music:ServerName"] ?? Environment.MachineName,
    BindAddress = bind, AdvertisedAddress = advertised == null ? null : new Uri(advertised),
    PrivateDirectory = data,
    ConnectionFile = Path.GetFullPath(config["connection-file"] ?? config["Music:ConnectionFile"] ?? Path.Combine(data,"connection.json")),
    AllowPlainHttp = config.GetValue("Distributed:AllowPlainHttp", false),
    TrustedProxies = (config.GetSection("Distributed:TrustedProxies").Get<string[]>() ?? []).Select(IPAddress.Parse).ToArray()
};
builder.Services.AddAIKernelCore(config).WithDistributedApplication();
var cfg = config["node-config"] ?? config["Music:NodeConfig"];
var capabilities=config.GetSection("Music:Capabilities").Get<Dictionary<string,string[]>>()??new();
if (cfg!=null) { var localNode=WorkerNode.Load(cfg); capabilities.TryAdd(localNode.node,localNode.types); }
builder.Services.AddSingleton(new MusicStore(data,capabilities));
builder.Services.AddSingleton<IMusicLibrary>(sp => sp.GetRequiredService<MusicStore>());
builder.Services.AddSingleton<IApiEndpointProvider, MusicEndpoints>();
builder.Services.AddSingleton<IMcpToolProvider, MusicTools>();
builder.Services.AddDistributedApplicationProvider(options);
using var host = builder.Build();
await host.StartAsync();
var extraHosts=new List<DistributedApplicationHost>();
try
{
    foreach(var address in addresses.Skip(1))
    {
        var extraOptions=new DistributedHostOptions {Service=options.Service,Name=options.Name,BindAddress=address,
            AdvertisedAddress=options.AdvertisedAddress??host.Services.GetRequiredService<IDistributedApplicationHost>().ListeningAddress,
            PrivateDirectory=data,ConnectionFile=options.ConnectionFile,AllowPlainHttp=options.AllowPlainHttp,TrustedProxies=options.TrustedProxies};
        var extra=ActivatorUtilities.CreateInstance<DistributedApplicationHost>(host.Services,extraOptions);
        extraHosts.Add(extra); await extra.StartAsync();
    }
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    var shutdown = host.WaitForShutdownAsync();
    if (cfg != null)
    {
        var listener = host.Services.GetRequiredService<IDistributedApplicationHost>().ListeningAddress!;
        var local = new UriBuilder(listener);
        if (local.Host is "0.0.0.0" or "[::]" or "::") local.Host = "127.0.0.1";
        var workerToken = await host.Services.GetRequiredService<ApplicationCredentials>().GetSecretAsync("worker");
        var worker = MusicWorker.ServeFile(cfg, lifetime.ApplicationStopping, local.Uri.AbsoluteUri, workerToken);
        if (await Task.WhenAny(shutdown,worker) == worker) await worker;
        await shutdown;
        await worker;
    }
    else await shutdown;
}
finally { foreach(var extra in extraHosts) await extra.DisposeAsync(); await host.StopAsync(); }
