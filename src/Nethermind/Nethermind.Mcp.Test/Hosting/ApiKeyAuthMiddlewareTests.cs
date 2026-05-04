// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Mcp.Hosting;
using NUnit.Framework;

namespace Nethermind.Mcp.Test.Hosting;

public class ApiKeyAuthMiddlewareTests
{
    private static (ApiKeyAuthMiddleware middleware, NextSpy spy) Build(string? apiKey)
    {
        McpConfig config = new() { ApiKey = apiKey };
        NextSpy spy = new();
        ApiKeyAuthMiddleware middleware = new(spy.RequestDelegate, config);
        return (middleware, spy);
    }

    private static DefaultHttpContext NewContext(string? authorizationHeader = null)
    {
        DefaultHttpContext ctx = new();
        if (authorizationHeader is not null)
        {
            ctx.Request.Headers["Authorization"] = authorizationHeader;
        }
        return ctx;
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task When_apiKey_is_unset_request_passes_through(string? apiKey)
    {
        (ApiKeyAuthMiddleware middleware, NextSpy spy) = Build(apiKey: apiKey);
        DefaultHttpContext ctx = NewContext();

        await middleware.InvokeAsync(ctx);

        Assert.That(spy.Called, Is.True);
        Assert.That(ctx.Response.StatusCode, Is.Not.EqualTo(StatusCodes.Status401Unauthorized));
    }

    [Test]
    public async Task When_apiKey_is_set_and_header_matches_request_passes()
    {
        (ApiKeyAuthMiddleware middleware, NextSpy spy) = Build(apiKey: "secret");
        DefaultHttpContext ctx = NewContext("Bearer secret");

        await middleware.InvokeAsync(ctx);

        Assert.That(spy.Called, Is.True);
        Assert.That(ctx.Response.StatusCode, Is.Not.EqualTo(StatusCodes.Status401Unauthorized));
    }

    [Test]
    public async Task When_apiKey_is_set_and_header_missing_returns_401()
    {
        (ApiKeyAuthMiddleware middleware, NextSpy spy) = Build(apiKey: "secret");
        DefaultHttpContext ctx = NewContext();

        await middleware.InvokeAsync(ctx);

        Assert.That(spy.Called, Is.False);
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    [Test]
    public async Task When_apiKey_is_set_and_bearer_value_wrong_returns_401()
    {
        (ApiKeyAuthMiddleware middleware, NextSpy spy) = Build(apiKey: "secret");
        DefaultHttpContext ctx = NewContext("Bearer wrong");

        await middleware.InvokeAsync(ctx);

        Assert.That(spy.Called, Is.False);
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    [Test]
    public async Task Header_match_is_case_insensitive_for_scheme_but_case_sensitive_for_token()
    {
        (ApiKeyAuthMiddleware middleware1, NextSpy spy1) = Build(apiKey: "secret");
        DefaultHttpContext ctx1 = NewContext("bearer secret");

        await middleware1.InvokeAsync(ctx1);

        Assert.That(spy1.Called, Is.True, "lowercase scheme with matching token should pass");
        Assert.That(ctx1.Response.StatusCode, Is.Not.EqualTo(StatusCodes.Status401Unauthorized));

        (ApiKeyAuthMiddleware middleware2, NextSpy spy2) = Build(apiKey: "secret");
        DefaultHttpContext ctx2 = NewContext("Bearer SECRET");

        await middleware2.InvokeAsync(ctx2);

        Assert.That(spy2.Called, Is.False, "uppercase token should not match lowercase configured key");
        Assert.That(ctx2.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    private sealed class NextSpy
    {
        public bool Called { get; private set; }

        public Task RequestDelegate(HttpContext _)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }
}
