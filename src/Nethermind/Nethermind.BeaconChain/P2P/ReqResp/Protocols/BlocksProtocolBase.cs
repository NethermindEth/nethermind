// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>Chunked block response plumbing shared by the <c>beacon_blocks_by_range</c> and <c>beacon_blocks_by_root</c> protocols.</summary>
public abstract class BlocksProtocolBase(BeaconChainSpec spec) : ReqRespProtocolBase
{
    /// <summary>The spec <c>MAX_REQUEST_BLOCKS_DENEB</c>.</summary>
    public const ulong MaxRequestBlocks = 128;

    protected BeaconChainSpec Spec { get; } = spec;

    /// <summary>The context bytes of a block chunk: the fork digest of the block's slot epoch.</summary>
    protected byte[] ContextBytesFor(SignedBeaconBlock block) => ForkDigest.Compute(Spec, Spec.GetEpoch(block.Message!.Slot));

    protected async Task<IReadOnlyList<SignedBeaconBlock>> ReadBlockChunksAsync(Stream stream, int maxBlocks)
    {
        List<SignedBeaconBlock> blocks = [];
        using CancellationTokenSource cts = StartTimeout(TtfbTimeout + RespTimeout);
        while (await ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, cts.Token) is { } chunk)
        {
            if (chunk.Result != ReqRespFraming.ResponseCode.Success)
            {
                throw ErrorChunkToException(chunk);
            }

            if (blocks.Count >= maxBlocks)
            {
                throw new Eth2ReqRespException($"Peer responded with more than the requested {maxBlocks} blocks");
            }

            SignedBeaconBlock block;
            try
            {
                SignedBeaconBlock.Decode(chunk.Payload, out block);
            }
            catch (Exception e) when (e is not Eth2ReqRespException and not OperationCanceledException)
            {
                throw new Eth2ReqRespException($"Malformed block chunk: {e.Message}");
            }

            if (!chunk.ContextBytes.AsSpan().SequenceEqual(ContextBytesFor(block)))
            {
                throw new Eth2ReqRespException($"Block chunk context bytes do not match the fork digest of slot {block.Message!.Slot}");
            }

            blocks.Add(block);
            cts.CancelAfter(RespTimeout);
        }

        return blocks;
    }

    protected Task WriteBlockChunkAsync(Stream stream, SignedBeaconBlock block, CancellationTokenSource cts)
    {
        cts.CancelAfter(RespTimeout);
        return ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, ContextBytesFor(block), SignedBeaconBlock.Encode(block), cts.Token);
    }
}
