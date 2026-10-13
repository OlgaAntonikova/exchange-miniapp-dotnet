using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Exchange.Api.Web;

/// <summary>
/// The Mini App's own files (spec 1.7): <c>wwwroot</c> served at <c>/static</c>, and
/// <c>GET /</c> answering <c>index.html</c>. Every answer carries <c>Cache-Control: no-cache</c>, so a
/// phone revalidates after an update. Only files under <c>wwwroot</c> can be reached.
/// </summary>
public static class FrontEnd
{
    /// <summary>The path the files are served under.</summary>
    public const string StaticPath = "/static";

    /// <summary>The page <c>GET /</c> answers.</summary>
    public const string PageFile = "index.html";

    /// <summary>The cache rule of every front-end answer.</summary>
    public const string CacheControl = "no-cache";

    private const string PageContentType = "text/html; charset=utf-8";

    /// <summary>Serves <c>wwwroot</c> at <c>/static</c>.</summary>
    public static IApplicationBuilder UseFrontEndFiles(this IApplicationBuilder app, IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return app.UseStaticFiles(new StaticFileOptions
        {
            RequestPath = StaticPath,
            FileProvider = environment.WebRootFileProvider,
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
            OnPrepareResponse = context => context.Context.Response.Headers[HeaderNames.CacheControl] = CacheControl,
        });
    }

    /// <summary>Maps <c>GET /</c> to the page.</summary>
    public static IEndpointRouteBuilder MapFrontEndPage(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (HttpContext http, IWebHostEnvironment environment) =>
        {
            http.Response.Headers[HeaderNames.CacheControl] = CacheControl;
            return Results.File(environment.WebRootFileProvider.GetFileInfo(PageFile).PhysicalPath!, PageContentType);
        });
        return app;
    }
}
