using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.VisualSearch.Web;

public sealed class IndexHtmlScriptMiddleware
{
    private const string EndMarker = "<!-- VisualSearch:end -->";
    // Keep the script at the end of the document and load it synchronously. Some
    // embedded WebViews do not reliably execute a deferred script after a cached
    // index document is rewritten. The version query also busts an old script.
    private const string Snippet = "\n<!-- VisualSearch:begin --><script src=\"../VisualSearch/ClientScript?v=0.1.0.12\"></script><!-- VisualSearch:end -->\n";
    private readonly RequestDelegate _next;
    public IndexHtmlScriptMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IsIndex(context.Request.Path.Value))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // A WebView can keep an old index.html from before the plugin was
        // installed. If its conditional request is allowed through unchanged,
        // the server returns 304 and the old document never receives the
        // plugin script. Force a fresh 200 only for the two HTML entry paths;
        // the response handling below still refuses to write bodies for any
        // 304/204 produced by downstream middleware.
        context.Request.Headers.Remove(HeaderNames.IfNoneMatch);
        context.Request.Headers.Remove(HeaderNames.IfModifiedSince);
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);
        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try { await _next(context).ConfigureAwait(false); }
        finally { context.Response.Body = original; }
        var bytes = buffer.ToArray();
        var rewritten = false;
        if (context.Response.StatusCode == StatusCodes.Status200OK
            && context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            var html = Encoding.UTF8.GetString(bytes);
            var beginMarker = html.IndexOf("<!-- VisualSearch:begin", StringComparison.OrdinalIgnoreCase);
            var endMarker = beginMarker >= 0 ? html.IndexOf(EndMarker, beginMarker, StringComparison.OrdinalIgnoreCase) : -1;
            if (beginMarker >= 0 && endMarker >= 0)
            {
                endMarker += EndMarker.Length;
                var replacement = Snippet.Trim('\r', '\n');
                html = html.Substring(0, beginMarker) + replacement + html.Substring(endMarker);
                bytes = Encoding.UTF8.GetBytes(html);
                context.Response.Headers.Remove(HeaderNames.ETag);
                context.Response.ContentLength = bytes.Length;
                rewritten = true;
            }
            else if (beginMarker < 0)
            {
                var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                html = at >= 0 ? html.Insert(at, Snippet) : html + Snippet;
                bytes = Encoding.UTF8.GetBytes(html);
                context.Response.Headers.Remove(HeaderNames.ETag);
                context.Response.ContentLength = bytes.Length;
                rewritten = true;
            }
        }

        // 304 (and other bodyless responses) must not receive a response body. For an
        // ordinary non-rewritten response, preserve the buffered body exactly as the
        // downstream middleware produced it.
        if (context.Response.StatusCode == StatusCodes.Status304NotModified
            || context.Response.StatusCode == StatusCodes.Status204NoContent)
        {
            return;
        }

        if (rewritten || bytes.Length > 0)
            await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsIndex(string? path) => !string.IsNullOrEmpty(path)
        && (path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase));
}
