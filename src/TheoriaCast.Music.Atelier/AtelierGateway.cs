using System.Net;
using System.Text.Json;
using AIKernel.Abstractions.Hosting;
using AIKernel.Dtos.Hosting;
using AIKernel.Providers.Distributed;
using Microsoft.AspNetCore.Http.Features;

namespace TheoriaCast.Music.Atelier;

internal static class AtelierGateway
{
    private static readonly string[] ApiPaths = ["/jobs","/tracks","/nodes","/works","/studio/requests","/composition","/mcp","/heartbeat"];
    private static readonly string[] RelayHeaders = ["Accept","Range","If-Range","If-None-Match","If-Modified-Since",
        "MCP-Protocol-Version","Mcp-Session-Id","Last-Event-ID"];

    public static async Task Run(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args=args, ContentRootPath=AppContext.BaseDirectory });
        var port=builder.Configuration.GetValue("port",39395);
        if (port is <1 or >65535) throw new ArgumentException("待受ポートを確認してください。");
        builder.WebHost.UseUrls("http://localhost:"+port);
        var profile=builder.Configuration["profile"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TheoriaCast","Atelier");
        builder.Services.AddDistributedApplicationClient(profile,builder.Configuration.GetValue("Distributed:AllowPlainHttp",false));
        var app=builder.Build();
        var store=app.Services.GetRequiredService<IConnectionProfileStore>();
        var files=app.Services.GetRequiredService<IConnectionDescriptorProvider>();
        var monitor=app.Services.GetRequiredService<IHeartbeatProvider>();
        var client=app.Services.GetRequiredService<IConnectionClient>();

        void Import(Stream content)
        {
            var descriptor=files.Read(content);
            var entry=files.Validate(descriptor);
            if (entry.Service!="TheoriaCast.Music.Api" || entry.ApiUrl.IsLoopback && entry.ApiUrl.Port==port)
                throw new InvalidDataException("作曲サーバーの接続ファイルを指定してください。");
            content.Position=0;
            store.Import(content);
        }
        if (builder.Configuration["import"] is string file) { using var input=File.OpenRead(file); Import(input); }
        object Overview() => new {
            selected=store.Selected?.ToString("N"),
            servers=store.List().Select(s=>{
                var h=monitor.Get(s.ServerId);
                return new {server_id=s.ServerId.ToString("N"),name=s.Name,api_url=s.ApiUrl.AbsoluteUri,
                    heartbeat=new {state=h.State switch {ConnectionState.Online=>"online",ConnectionState.Unauthorized=>"unauthorized",
                        ConnectionState.IdentityMismatch=>"mismatch",ConnectionState.Unavailable=>"unavailable",_=>"checking"},
                        checked_at=h.CheckedAt,last_seen=h.LastSuccess}};
            }).ToArray()
        };
        app.Use(async (ctx,next)=>{
            if (ctx.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]") ||
                (ctx.Request.Headers.Origin.Count!=0 && !string.Equals(ctx.Request.Headers.Origin.ToString(),
                    "http://"+ctx.Request.Host,StringComparison.OrdinalIgnoreCase))) { ctx.Response.StatusCode=403;return; }
            await next();
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.MapGet("/atelier/servers",()=>Overview());
        app.MapPost("/atelier/servers",async (HttpContext ctx)=>{
            if (!ctx.Request.HasJsonContentType()) return Results.StatusCode(415);
            var limits=ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limits is {IsReadOnly:false}) limits.MaxRequestBodySize=16384;
            using var data=new MemoryStream();
            await ctx.Request.Body.CopyToAsync(data,ctx.RequestAborted);
            data.Position=0;
            try { Import(data);return Results.Ok(Overview()); }
            catch(Exception e) when (e is InvalidDataException or ArgumentException or JsonException)
            { return Results.BadRequest(new {error="この作曲サーバーの接続ファイルを読み込めません。"}); }
        });
        app.MapPost("/atelier/servers/{id:guid}/select",(Guid id)=>{
            try { store.Select(id);return Results.Ok(Overview()); }
            catch(KeyNotFoundException) { return Results.NotFound(); }
        });
        app.MapFallback("/{**path}",async (HttpContext ctx)=>{
            var path=ctx.Request.Path;
            if (!ApiPaths.Any(p=>path.StartsWithSegments(p))) { ctx.Response.StatusCode=404;return; }
            if (store.Selected is not {} selected) { ctx.Response.StatusCode=503;return; }
            if (ctx.Request.Headers.TryGetValue("X-Theoria-Server",out var expected) &&
                (!Guid.TryParse(expected.ToString(),out var pinned) || pinned!=selected)) { ctx.Response.StatusCode=409;return; }
            var uri=new Uri(path.ToUriComponent().TrimStart('/')+ctx.Request.QueryString.ToUriComponent(),UriKind.Relative);
            using var request=new HttpRequestMessage(new HttpMethod(ctx.Request.Method),uri);
            if (ctx.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody==true)
            {
                request.Content=new StreamContent(ctx.Request.Body);
                if (ctx.Request.ContentType!=null) request.Content.Headers.TryAddWithoutValidation("Content-Type",ctx.Request.ContentType);
                request.Content.Headers.ContentLength=ctx.Request.ContentLength;
            }
            foreach (var name in RelayHeaders)
                if (ctx.Request.Headers.TryGetValue(name,out var values)) request.Headers.TryAddWithoutValidation(name,values.ToArray());
            try
            {
                using var response=await client.SendRequestAsync(selected,request,ctx.RequestAborted);
                ctx.Response.StatusCode=(int)response.StatusCode;
                foreach(var header in response.Headers.Concat(response.Content.Headers))
                    if (!header.Key.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase) &&
                        !header.Key.Equals("Connection",StringComparison.OrdinalIgnoreCase) &&
                        !header.Key.Equals("Set-Cookie",StringComparison.OrdinalIgnoreCase))
                        ctx.Response.Headers[header.Key]=header.Value.ToArray();
                ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                await ctx.Response.StartAsync(ctx.RequestAborted);
                await response.Content.CopyToAsync(ctx.Response.Body,ctx.RequestAborted);
            }
            catch (HttpRequestException) when (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode=(int)HttpStatusCode.BadGateway;
                await ctx.Response.WriteAsJsonAsync(new {error="作曲サーバーに接続できません。"});
            }
        });
        if (Environment.GetEnvironmentVariable("THEORIA_STUDIO_OPEN")=="1")
            app.Lifetime.ApplicationStarted.Register(()=>{
                var url="http://localhost:"+port;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true}); }
                catch(System.ComponentModel.Win32Exception) { Console.WriteLine(url); }
            });
        await app.RunAsync();
    }
}
