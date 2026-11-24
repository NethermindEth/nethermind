// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Maps the routes a service declares on its own methods.</summary>
internal static class ServiceEndpointMapper
{
    /// <summary>Maps each public method of <paramref name="service"/> once per <see cref="HttpMethodAttribute"/> it carries.</summary>
    /// <remarks>Parameters are bound as for a minimal API; an instance method is called on the <paramref name="service"/> the request's scope resolves.</remarks>
    public static void MapService(this IEndpointRouteBuilder app, Type service)
    {
        foreach (MethodInfo method in service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            foreach (HttpMethodAttribute route in method.GetCustomAttributes<HttpMethodAttribute>())
            {
                app.MapMethod(service, method, route);
            }
        }
    }

    private static void MapMethod(this IEndpointRouteBuilder app, Type service, MethodInfo method, HttpMethodAttribute route)
    {
        RoutePattern pattern = RoutePatternFactory.Parse(route.Template!);
        RequestDelegateResult handler = RequestDelegateFactory.Create(
            method,
            method.IsStatic ? null : c => c.RequestServices.GetRequiredService(service),
            new RequestDelegateFactoryOptions
            {
                ServiceProvider = app.ServiceProvider,
                RouteParameterNames = pattern.Parameters.Select(static parameter => parameter.Name),
                ThrowOnBadRequest = true,
            });

        IEndpointConventionBuilder endpoint = app.MapMethods(route.Template!, route.HttpMethods, handler.RequestDelegate)
            .WithMetadata([.. handler.EndpointMetadata, .. method.GetCustomAttributes<NegotiatedAttribute>()]);
        if (handler.EndpointMetadata.OfType<IAcceptsMetadata>().Any())
        {
            // Keeps routing from matching on Content-Type, which would hide a mismatch behind the fallback's 404.
            endpoint.WithMetadata(new AcceptsMetadata(["*/*"]));
        }
    }
}
