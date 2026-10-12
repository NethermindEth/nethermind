// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Nethermind.LightClient;

internal static class BrowserHost
{
    internal static void Configure(WebApplication app, string? directory, string? allowedOrigin, string rpcUrls)
    {
        Uri? origin = allowedOrigin is null ? null : ParseOrigin(allowedOrigin);
        bool loopbackOnly = rpcUrls.Split(';').All(static value => Uri.TryCreate(value, UriKind.Absolute, out Uri? url) && url.IsLoopback);
        app.Use(async (context, next) =>
        {
            if (loopbackOnly && (!Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}", UriKind.Absolute, out Uri? host) || !host.IsLoopback))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            string requestOrigin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(requestOrigin))
            {
                string localOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
                bool local = string.Equals(requestOrigin, localOrigin, StringComparison.OrdinalIgnoreCase);
                bool allowed = origin is not null && string.Equals(requestOrigin, origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
                if (!local && !allowed)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                context.Response.Headers.AccessControlAllowOrigin = requestOrigin;
                context.Response.Headers.Vary = "Origin";
                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.Headers.AccessControlAllowMethods = "GET, POST";
                    context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }
            }
            await next(context);
        });
        if (directory is null) return;
        PhysicalFileProvider files = new(Path.GetFullPath(directory));
        app.Lifetime.ApplicationStopped.Register(files.Dispose);
        FileExtensionContentTypeProvider contentTypes = new();
        contentTypes.Mappings[".wasm"] = "application/wasm";
        contentTypes.Mappings[".dat"] = "application/octet-stream";
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals("/browser", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Redirect("/browser/");
                return;
            }
            await next(context);
        });
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files, RequestPath = "/browser" });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            RequestPath = "/browser",
            ContentTypeProvider = contentTypes,
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; worker-src 'self'; connect-src 'self' https:; style-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'";
                context.Context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Context.Response.Headers["Referrer-Policy"] = "no-referrer";
            },
        });
    }

    private static Uri ParseOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? origin) ||
            origin.Scheme != "https" && (origin.Scheme != "http" || !origin.IsLoopback) ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new ArgumentException("Browser origin must be an HTTPS origin or loopback HTTP origin without a path.");
        return origin;
    }
}
