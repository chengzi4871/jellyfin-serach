using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.VisualSearch.Web;

public sealed class IndexHtmlScriptMiddleware
{
    private const string Marker = "VisualSearch:begin";
    private const string Snippet = "\n<!-- VisualSearch:begin --><script src=\"../VisualSearch/ClientScript\" defer></script><!-- VisualSearch:end -->\n";
    private readonly RequestDelegate _next;
    public IndexHtmlScriptMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IsIndex(context.Request.Path.Value)
            || context.Request.Headers.ContainsKey(HeaderNames.IfNoneMatch)
            || context.Request.Headers.ContainsKey(HeaderNames.IfModifiedSince))
        {
            // Conditional requests must flow through unchanged so a 304 never gets a body.
            await _next(context).ConfigureAwait(false);
            return;
        }
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
            if (!html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
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
