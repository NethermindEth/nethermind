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

/// <summary>
/// Creates a factory for creating <see cref="HttpContext" /> instances.
/// </summary>
/// <param name="serviceProvider">The <see cref="IServiceProvider"/> to be used when retrieving services.</param>
public class HttpContextFactory(IServiceProvider serviceProvider)
{
    private readonly IHttpContextAccessor? _httpContextAccessor = serviceProvider.GetService<IHttpContextAccessor>();
    private readonly FormOptions _formOptions = serviceProvider.GetRequiredService<IOptions<FormOptions>>().Value;
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    internal IHttpContextAccessor? HttpContextAccessor => _httpContextAccessor;

    /// <summary>
    /// Create an <see cref="HttpContext"/> instance given an <paramref name="featureCollection" />.
    /// </summary>
    /// <param name="featureCollection"></param>
    /// <returns>An initialized <see cref="HttpContext"/> object.</returns>
    public HttpContext Create(IFeatureCollection featureCollection)
    {
        ArgumentNullException.ThrowIfNull(featureCollection);

        DefaultHttpContext httpContext = new(featureCollection);
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
