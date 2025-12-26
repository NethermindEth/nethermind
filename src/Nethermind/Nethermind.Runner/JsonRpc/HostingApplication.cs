// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nethermind.Logging;

namespace Nethermind.Runner.JsonRpc;

internal sealed class HostingApplication : IHttpApplication<HostingApplication.Context>
{
    private readonly ILogger _logger;
    private readonly RequestDelegate _application;
    private readonly HttpContextFactory? _httpContextFactory;

    public HostingApplication(
        RequestDelegate application,
        ILogManager logManager,
        HttpContextFactory httpContextFactory)
    {
        _logger = logManager.GetClassLogger();
        //_logManager = logManager;
        _application = application;
        _httpContextFactory = httpContextFactory;
    }

    // Set up the request
    public Context CreateContext(IFeatureCollection contextFeatures)
    {
        Context? hostContext;
        if (contextFeatures is IHostContextContainer<Context> container)
        {
            hostContext = container.HostContext;
            if (hostContext is null)
            {
                hostContext = new Context();
                container.HostContext = hostContext;
            }
        }
        else
        {
            // Server doesn't support pooling, so create a new Context
            hostContext = new Context();
        }

        HttpContext httpContext;
        var defaultHttpContext = (DefaultHttpContext?)hostContext.HttpContext;
        if (defaultHttpContext is null)
        {
            httpContext = _httpContextFactory.Create(contextFeatures);
            hostContext.HttpContext = httpContext;
        }
        else
        {
            _httpContextFactory.Initialize(defaultHttpContext, contextFeatures);
            httpContext = defaultHttpContext;
        }

        return hostContext;
    }

    // Execute the request
    public Task ProcessRequestAsync(Context context)
    {
        return _application(context.HttpContext!);
    }

    // Clean up the request
    public void DisposeContext(Context context, Exception? exception)
    {
        var httpContext = context.HttpContext!;

        _httpContextFactory.Dispose((DefaultHttpContext)httpContext);

        if (_httpContextFactory.HttpContextAccessor != null)
            // Clear the HttpContext if the accessor was used. It's likely that the lifetime extends
            // past the end of the http request and we want to avoid changing the reference from under
            // consumers.
            context.HttpContext = null;

        // Reset the context as it may be pooled
        context.Reset();
    }

    internal sealed class Context
    {
        public HttpContext? HttpContext { get; set; }
        public IDisposable? Scope { get; set; }

        public void Reset()
        {
            Scope = null;
        }
    }
}
