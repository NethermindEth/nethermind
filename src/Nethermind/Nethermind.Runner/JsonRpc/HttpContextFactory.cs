// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Nethermind.Runner.JsonRpc;

public class HttpContextFactory
{
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly FormOptions _formOptions;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    // This takes the IServiceProvider because it needs to support an ever expanding
    // set of services that flow down into HttpContext features
    /// <summary>
    /// Creates a factory for creating <see cref="HttpContext" /> instances.
    /// </summary>
    /// <param name="serviceProvider">The <see cref="IServiceProvider"/> to be used when retrieving services.</param>
    public HttpContextFactory(IServiceProvider serviceProvider)
    {
        // May be null
        _httpContextAccessor = serviceProvider.GetService<IHttpContextAccessor>();
        _formOptions = serviceProvider.GetRequiredService<IOptions<FormOptions>>().Value;
        _serviceScopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
    }

    internal IHttpContextAccessor? HttpContextAccessor => _httpContextAccessor;

    /// <summary>
    /// Create an <see cref="HttpContext"/> instance given an <paramref name="featureCollection" />.
    /// </summary>
    /// <param name="featureCollection"></param>
    /// <returns>An initialized <see cref="HttpContext"/> object.</returns>
    public HttpContext Create(IFeatureCollection featureCollection)
    {
        ArgumentNullException.ThrowIfNull(featureCollection);

        var httpContext = new DefaultHttpContext(featureCollection);
        Initialize(httpContext, featureCollection);
        return httpContext;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Initialize(DefaultHttpContext httpContext, IFeatureCollection featureCollection)
    {
        Debug.Assert(featureCollection != null);
        Debug.Assert(httpContext != null);

        httpContext.Initialize(featureCollection);

        if (_httpContextAccessor is not null)
        {
            _httpContextAccessor.HttpContext = httpContext;
        }

        httpContext.FormOptions = _formOptions;
        httpContext.ServiceScopeFactory = _serviceScopeFactory;
    }

    /// <summary>
    /// Clears the current <see cref="HttpContext" />.
    /// </summary>
    public void Dispose(HttpContext httpContext)
    {
        if (_httpContextAccessor is not null)
        {
            _httpContextAccessor.HttpContext = null;
        }
    }

    internal void Dispose(DefaultHttpContext httpContext)
    {
        if (_httpContextAccessor is not null)
        {
            _httpContextAccessor.HttpContext = null;
        }

        httpContext.Uninitialize();
    }
}
