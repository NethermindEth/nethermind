// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Modules;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[TestFixture]
public class JsonRpcContextResponseHeadersTests
{
    [TearDown]
    public void TearDown()
    {
        // Clean up any context from previous tests
        JsonRpcContext.Current.Value?.Dispose();
        JsonRpcContext.Current.Value = null;
    }

    [Test]
    public void SetResponseHeader_WhenContextExists_AddsHeader()
    {
        using var context = new JsonRpcContext(RpcEndpoint.Http);

        JsonRpcContext.SetResponseHeader("X-Arb-Gas-Used", "12345");

        Assert.That(context.ResponseHeaders, Is.Not.Null);
        Assert.That(context.ResponseHeaders!["X-Arb-Gas-Used"], Is.EqualTo("12345"));
    }

    [Test]
    public void SetResponseHeader_WhenContextIsNull_DoesNotThrow()
    {
        JsonRpcContext.Current.Value = null;

        Assert.DoesNotThrow(() => JsonRpcContext.SetResponseHeader("X-Arb-Gas-Used", "12345"));
    }

    [Test]
    public void SetResponseHeader_WhenCalledMultipleTimes_OverwritesPreviousValue()
    {
        using var context = new JsonRpcContext(RpcEndpoint.Http);

        JsonRpcContext.SetResponseHeader("X-Arb-Gas-Used", "12345");
        JsonRpcContext.SetResponseHeader("X-Arb-Gas-Used", "67890");

        Assert.That(context.ResponseHeaders!["X-Arb-Gas-Used"], Is.EqualTo("67890"));
    }

    [Test]
    public void SetResponseHeader_WhenCalledWithMultipleHeaders_AddsAllHeaders()
    {
        using var context = new JsonRpcContext(RpcEndpoint.Http);

        JsonRpcContext.SetResponseHeader("X-Arb-Gas-Used", "12345");
        JsonRpcContext.SetResponseHeader("X-Arb-Block-Time", "1234567890");
        JsonRpcContext.SetResponseHeader("X-Arb-L1-Block", "100");

        Assert.That(context.ResponseHeaders!.Count, Is.EqualTo(3));
        Assert.That(context.ResponseHeaders["X-Arb-Gas-Used"], Is.EqualTo("12345"));
        Assert.That(context.ResponseHeaders["X-Arb-Block-Time"], Is.EqualTo("1234567890"));
        Assert.That(context.ResponseHeaders["X-Arb-L1-Block"], Is.EqualTo("100"));
    }

    [Test]
    public void ResponseHeaders_ByDefault_IsNull()
    {
        using var context = new JsonRpcContext(RpcEndpoint.Http);

        Assert.That(context.ResponseHeaders, Is.Null);
    }

    [Test]
    public void SetResponseHeader_LazilyCreatesResponseHeaders()
    {
        using var context = new JsonRpcContext(RpcEndpoint.Http);
        Assert.That(context.ResponseHeaders, Is.Null);

        JsonRpcContext.SetResponseHeader("X-Test", "value");

        Assert.That(context.ResponseHeaders, Is.Not.Null);
    }

    [Test]
    public void SetResponseHeader_WithHttpEndpoint_WorksCorrectly()
    {
        // Test HTTP endpoint
        using var httpContext = new JsonRpcContext(RpcEndpoint.Http);
        JsonRpcContext.SetResponseHeader("X-Http-Header", "http-value");
        Assert.That(httpContext.ResponseHeaders!["X-Http-Header"], Is.EqualTo("http-value"));
    }

    [Test]
    public void SetResponseHeader_WithWsEndpoint_WorksCorrectly()
    {
        // Test WebSocket endpoint
        using var wsContext = new JsonRpcContext(RpcEndpoint.Ws);
        JsonRpcContext.SetResponseHeader("X-Ws-Header", "ws-value");
        Assert.That(wsContext.ResponseHeaders!["X-Ws-Header"], Is.EqualTo("ws-value"));
    }

    [Test]
    public void ContextDispose_ClearsCurrentContext()
    {
        var context = new JsonRpcContext(RpcEndpoint.Http);
        JsonRpcContext.SetResponseHeader("X-Test", "value");

        context.Dispose();

        JsonRpcContext.SetResponseHeader("X-Test-After", "value");
        // The disposed context should not have the new header
        Assert.That(context.ResponseHeaders!.TryGetValue("X-Test", out _), Is.True);
        Assert.That(context.ResponseHeaders!.TryGetValue("X-Test-After", out _), Is.False);
    }
}
