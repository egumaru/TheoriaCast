using AIKernel.Abstractions.Hosting;
using AIKernel.Dtos.Hosting;
using AIKernel.Providers.Distributed;
using Microsoft.AspNetCore.Http.Features;
using TheoriaCast.Visual;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var port = builder.Configuration.GetValue("port", 39416);
builder.WebHost.UseUrls($"http://localhost:{port}");
var profile = builder.Configuration["profile"] ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheoriaCast", "VisualAtelier");
builder.Services.AddDistributedApplicationClient(profile, builder.Configuration.GetValue("Distributed:AllowPlainHttp", false));
var app = builder.Build();
var store = app.Services.GetRequiredService<IConnectionProfileStore>();
var files = app.Services.GetRequiredService<IConnectionDescriptorProvider>();
var monitor = app.Services.GetRequiredService<IHeartbeatProvider>();
var client = app.Services.GetRequiredService<IConnectionClient>();

void Import(Stream input)
{
    var descriptor = files.Read(input);
    var connection = files.Validate(descriptor);
    if (connection.Service != VisualProtocol.Service || connection.ApiUrl.IsLoopback && connection.ApiUrl.Port == port)
        throw new InvalidDataException("素材のアトリエ用の接続ファイルを指定してください。");
    input.Position = 0;
    store.Import(input);
}

if (builder.Configuration["import"] is { } file)
{
    using var input = File.OpenRead(file);
    Import(input);
}

object Connections() => new
{
    selected = store.Selected?.ToString("N"),
    servers = store.List().Where(server => server.Service == VisualProtocol.Service).Select(server => new
    {
        id = server.ServerId.ToString("N"),
        name = server.Name,
        state = monitor.Get(server.ServerId).State switch
        {
            ConnectionState.Online => "online",
            ConnectionState.Unavailable => "unavailable",
            ConnectionState.Unauthorized => "unauthorized",
            ConnectionState.IdentityMismatch => "mismatch",
            _ => "checking"
        }
    }).ToArray()
};

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]") ||
        ctx.Request.Headers.Origin.Count != 0 && !string.Equals(ctx.Request.Headers.Origin.ToString(),
            "http://" + ctx.Request.Host, StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; img-src 'self' blob:";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/atelier/servers", Connections);
app.MapPost("/atelier/servers", async (HttpContext ctx) =>
{
    if (!ctx.Request.HasJsonContentType()) return Results.StatusCode(415);
    var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 16384;
    using var input = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(input, ctx.RequestAborted);
    input.Position = 0;
    try
    {
        Import(input);
        return Results.Ok(Connections());
    }
    catch (Exception e) when (e is ArgumentException or InvalidDataException)
    {
        return Results.BadRequest(new { error = "素材サーバーの接続ファイルを読み込めません。" });
    }
});
app.MapPost("/atelier/servers/{id:guid}", (Guid id) =>
{
    if (!store.List().Any(server => server.ServerId == id && server.Service == VisualProtocol.Service))
        return Results.NotFound();
    store.Select(id);
    return Results.Ok(Connections());
});
app.MapMethods(VisualProtocol.Prefix + "/{**path}", ["GET", "POST"], async (HttpContext ctx) =>
{
    var pinned = ctx.Request.Headers["X-Theoria-Server"].ToString();
    if (pinned.Length == 0) pinned = ctx.Request.Query["server"].ToString();
    if (store.Selected is not {} selected || !Guid.TryParseExact(pinned, "N", out var id) || id != selected
        || !store.List().Any(server => server.ServerId == selected && server.Service == VisualProtocol.Service))
    {
        ctx.Response.StatusCode = 409;
        return;
    }
    using var message = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), ctx.Request.Path.ToUriComponent().TrimStart('/'));
    if (ctx.Request.Method == "POST")
    {
        message.Content = new StreamContent(ctx.Request.Body);
        message.Content.Headers.ContentLength = ctx.Request.ContentLength;
        if (ctx.Request.ContentType is { } type) message.Content.Headers.TryAddWithoutValidation("Content-Type", type);
    }
    try
    {
        using var response = await client.SendRequestAsync(selected, message, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString();
        ctx.Response.Headers.CacheControl = "no-store";
        await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
    catch (HttpRequestException) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsJsonAsync(new { error = "素材サーバーに接続できません。" }, ctx.RequestAborted);
    }
});
await app.RunAsync();
