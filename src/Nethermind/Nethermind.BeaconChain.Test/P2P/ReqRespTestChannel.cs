// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

internal static class ReqRespTestChannel
{
    internal static ISessionContext Context(PeerId? requester = null)
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        Nethermind.Libp2p.Core.State state = new();
        if (requester is not null)
        {
            state.RemoteAddress = Multiaddress.Decode($"/ip4/127.0.0.1/tcp/4001/p2p/{requester}");
        }
        context.State.Returns(state);
        return context;
    }

    internal static async Task ListenThenCloseAsync(ISessionListenerProtocol protocol, IChannel channel, ISessionContext context)
    {
        await protocol.ListenAsync(channel, context);
        await channel.WriteEofAsync();
    }

    internal static Task StartListenerAsync(IChannel channel, Func<Task> listen, CancellationToken token) =>
        Task.Run(async () =>
        {
            try
            {
                await listen();
            }
            finally
            {
                await channel.WriteEofAsync(token);
            }
        }, token);

    internal static async Task AssertChunkLimitAsync<T>(MemoryStream response, int maximum, bool exceeds,
        Func<Task<IReadOnlyList<T>>> read, Func<long> limitFailures)
    {
        response.Position = 0;
        if (!exceeds)
        {
            Assert.That(await read(), Has.Count.EqualTo(maximum));
            return;
        }

        long before = limitFailures();
        Eth2ReqRespException thrown = Assert.ThrowsAsync<Eth2ReqRespException>(async () => await read())!;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(thrown.Message, Does.Contain(maximum.ToString()));
        Assert.That(limitFailures(), Is.EqualTo(before + 1), "limit violation recorded");
        Assert.That(response.Position, Is.LessThan(response.Length), "trailing response bytes remain unread");
    }

    // The libp2p host closes the response stream once the handler returns, even when it faults; the dial side reads until then.
    internal static async Task<List<ResponseChunk>> ReadResponseAsync(Func<IChannel, ISessionContext, Task> listen, byte[] requestSsz, CancellationToken token)
    {
        ISessionContext context = Context();
        Channel channel = new();
        Task listening = StartListenerAsync(channel.Reverse, () => listen(channel.Reverse, context), token);
        Stream stream = new ChannelStreamAdapter(channel);
        await ReqRespFraming.WriteRequestAsync(stream, requestSsz, token);
        await channel.WriteEofAsync(token);

        List<ResponseChunk> chunks = [];
        while (await ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, token) is { } chunk)
        {
            chunks.Add(chunk);
        }

        await listening.WaitAsync(token);
        return chunks;
    }
}
