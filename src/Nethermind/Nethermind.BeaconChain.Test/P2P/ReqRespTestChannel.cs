// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.Libp2p.Core;
using NSubstitute;

namespace Nethermind.BeaconChain.Test.P2P;

internal static class ReqRespTestChannel
{
    // The libp2p host closes the response stream once the handler returns, even when it faults; the dial side reads until then.
    internal static async Task<List<ResponseChunk>> ReadResponseAsync(Func<IChannel, ISessionContext, Task> listen, byte[] requestSsz, CancellationToken token)
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new Nethermind.Libp2p.Core.State());
        Channel channel = new();
        Task listening = Task.Run(async () =>
        {
            try
            {
                await listen(channel.Reverse, context);
            }
            finally
            {
                await channel.Reverse.WriteEofAsync(token);
            }
        }, token);
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
