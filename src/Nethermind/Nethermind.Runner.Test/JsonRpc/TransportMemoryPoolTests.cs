// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Config;
using Nethermind.Core.Authentication;
using Nethermind.Core.Memory;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Runner.JsonRpc;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Runner.Test.JsonRpc;

public class TransportMemoryPoolTests
{
    [Test]
    public void Rent_hands_out_a_whole_block_and_reuses_returned_ones([Values(-1, 1, 4096, TransportMemoryPool.BlockSize)] int minBufferSize)
    {
        using TransportMemoryPool pool = new();
        IMemoryOwner<byte> first = pool.Rent(minBufferSize);
        Memory<byte> firstMemory = first.Memory;
        first.Dispose();
        using IMemoryOwner<byte> second = pool.Rent(minBufferSize);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstMemory.Length, Is.EqualTo(TransportMemoryPool.BlockSize));
            Assert.That(second.Memory.Span == firstMemory.Span, Is.True, "a returned block is handed out again");
            Assert.That(pool.RetainedBlocks, Is.Zero);
        }
    }

    [Test]
    public void Rent_beyond_a_block_is_served_outside_the_pool()
    {
        using TransportMemoryPool pool = new();
        IMemoryOwner<byte> large = pool.Rent(TransportMemoryPool.BlockSize + 1);
        int length = large.Memory.Length;
        large.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(length, Is.EqualTo(TransportMemoryPool.BlockSize + 1));
            Assert.That(pool.RetainedBlocks, Is.Zero);
        }
    }

    [Test]
    public void A_block_goes_back_to_the_pool_once()
    {
        using TransportMemoryPool pool = new();
        IMemoryOwner<byte> owner = pool.Rent();
        owner.Dispose();
        owner.Dispose();

        using IMemoryOwner<byte> first = pool.Rent();
        using IMemoryOwner<byte> second = pool.Rent();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Memory.Span == second.Memory.Span, Is.False, "two owners must not share a block");
            Assert.That(pool.RetainedBlocks, Is.Zero);
        }
    }

    [Test]
    public void Keeps_at_most_the_retention_cap()
    {
        using TransportMemoryPool pool = new();
        List<IMemoryOwner<byte>> owners = [];
        for (int i = 0; i < TransportMemoryPool.MaxRetainedBlocks + 5; i++) owners.Add(pool.Rent());
        foreach (IMemoryOwner<byte> owner in owners) owner.Dispose();

        Assert.That(pool.RetainedBlocks, Is.EqualTo(TransportMemoryPool.MaxRetainedBlocks));
    }

    [Test]
    public void A_disposed_pool_refuses_rents_and_drops_returns()
    {
        TransportMemoryPool pool = new();
        IMemoryOwner<byte> owner = pool.Rent();
        pool.Rent().Dispose();
        pool.Dispose();
        owner.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.RetainedBlocks, Is.Zero);
            Assert.That(() => pool.Rent(), Throws.InstanceOf<ObjectDisposedException>());
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Startup_gives_kestrel_the_pool_and_large_bodies_cross_it_intact()
    {
        JsonRpcConfig rpcConfig = new();
        using GCKeeper gcKeeper = new(NoGCStrategy.Instance, LimboLogs.Instance);
        RpcModuleProvider moduleProvider = new(new RealFileSystem(), rpcConfig, new EthereumJsonSerializer(), LimboLogs.Instance);
        Bootstrap.Instance.JsonRpcService = new JsonRpcService(moduleProvider, LimboLogs.Instance, rpcConfig, gcKeeper);
        Bootstrap.Instance.LogManager = LimboLogs.Instance;
        Bootstrap.Instance.JsonSerializer = new EthereumJsonSerializer();
        Bootstrap.Instance.JsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
        Bootstrap.Instance.JsonRpcAuthentication = Substitute.For<IRpcAuthentication>();
        IConfigProvider configProvider = Substitute.For<IConfigProvider>();
        configProvider.GetConfig<IJsonRpcConfig>().Returns(rpcConfig);

        // In the order JsonRpcRunner builds its host: Kestrel first, then Startup's services.
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost
            .UseKestrelCore()
            .ConfigureKestrel(static options => options.Listen(IPAddress.Loopback, 0))
            .ConfigureServices(services =>
            {
                services.AddSingleton(configProvider);
                new Startup().ConfigureServices(services);
            });
        await using WebApplication app = builder.Build();
        app.Run(static async ctx =>
        {
            using MemoryStream body = new();
            await ctx.Request.Body.CopyToAsync(body);
            ctx.Response.ContentLength = body.Length;
            await ctx.Response.Body.WriteAsync(body.GetBuffer().AsMemory(0, (int)body.Length));
        });
        await app.StartAsync();

        byte[] sent = new byte[5 * TransportMemoryPool.BlockSize + 123];
        new Random(42).NextBytes(sent);
        Uri address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using HttpClient client = new();
        using HttpResponseMessage response = await client.PostAsync(address, new ByteArrayContent(sent));
        byte[] received = await response.Content.ReadAsByteArrayAsync();
        await app.StopAsync();

        IMemoryPoolFactory<byte> factory = app.Services.GetRequiredService<IMemoryPoolFactory<byte>>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory, Is.InstanceOf<TransportMemoryPoolFactory>());
            Assert.That((factory as TransportMemoryPoolFactory)?.PoolsCreated, Is.GreaterThan(0), "Kestrel did not take its pools from the factory");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(received, Is.EqualTo(sent));
        }
    }
}
