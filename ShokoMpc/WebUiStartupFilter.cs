using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ShokoMpc;

// Register before Shoko's static-file middleware. Nothing on disk is patched.
public sealed class WebUiStartupFilter : IStartupFilter
{
    private static readonly Assembly Assembly = typeof(Plugin).Assembly;
    private static readonly Dictionary<string, (string Resource, string Type)> Assets = new()
    {
        [Plugin.BasePath + "/dashboard"] = ("ShokoMpc.Web.dashboard.html", "text/html; charset=utf-8"),
        [Plugin.BasePath + "/webui.js"] = ("ShokoMpc.Web.webui.js", "text/javascript; charset=utf-8"),
        [Plugin.BasePath + "/Windows-Setup.zip"] = ("ShokoMpc.Windows.Windows-Setup.zip", "application/zip")
    };
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (c, continuation) =>
        {
            if ((HttpMethods.IsGet(c.Request.Method) || HttpMethods.IsHead(c.Request.Method)) &&
                Assets.TryGetValue(c.Request.Path.Value ?? "", out var asset))
            {
                using var content = Assembly.GetManifestResourceStream(asset.Resource);
                if (content is null) { c.Response.StatusCode = 404; return; }
                c.Response.ContentType = asset.Type;
                c.Response.Headers.CacheControl = "no-store";
                c.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (asset.Type == "application/zip")
                {
                    c.Response.Headers.ContentDisposition = "attachment; filename=ShokoMpc-Windows-Setup.zip";
                }

                if (!HttpMethods.IsHead(c.Request.Method))
                {
                    await content.CopyToAsync(c.Response.Body, c.RequestAborted);
                }

                return;
            }
            var path = c.Request.Path.Value ?? "";
            if (!HttpMethods.IsGet(c.Request.Method) || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                !(path == "/" || path.StartsWith("/webui", StringComparison.OrdinalIgnoreCase)) ||
                (Path.HasExtension(path) && !path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) ||
                !c.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
            { await continuation(); return; }
            var originalBody = c.Response.Body;
            var originalEncoding = c.Request.Headers.AcceptEncoding;
            // Only the small Web UI document is buffered, never media or API responses.
            c.Request.Headers.AcceptEncoding = "identity";
            await using var buffer = new MemoryStream();
            c.Response.Body = buffer;
            try
            {
                await continuation();
                c.Response.Body = originalBody;
                var bytes = buffer.ToArray();
                if (c.Response.StatusCode == 200 && c.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true &&
                    !c.Response.Headers.ContainsKey("Content-Encoding") && bytes.Length < 2_000_000)
                {
                    var html = Encoding.UTF8.GetString(bytes);
                    if (html.Contains("id=\"app-root\"") && !html.Contains("id=\"shoko-mpc-script\""))
                    {
                        var src = HtmlEncoder.Default.Encode(c.Request.PathBase + Plugin.BasePath + "/webui.js");
                        var script = "<script id=\"shoko-mpc-script\" defer src=\"" + src + "\"></script>";
                        html = html.Replace("</head>", script + "</head>", StringComparison.OrdinalIgnoreCase);
                        bytes = Encoding.UTF8.GetBytes(html);
                        c.Response.Headers.Remove("ETag");
                        c.Response.Headers.CacheControl = "no-store";
                        c.Response.ContentLength = bytes.Length;
                    }
                }
                await originalBody.WriteAsync(bytes, c.RequestAborted);
            }
            finally { c.Response.Body = originalBody; c.Request.Headers.AcceptEncoding = originalEncoding; }
        });
        next(app);
    };
}
