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

    /// <summary>
    /// Not a spec constant (the spec has no overall-response-deadline concept — see
    /// <see cref="StartBoundedTimeout"/>): bounds the total wall-clock time a streamed blocks
    /// response (or its being written) may take, closing a stream that would otherwise stay open
    /// indefinitely by keeping every individual chunk just under <see cref="ReqRespProtocolBase.RespTimeout"/>.
    /// </summary>
    protected static readonly TimeSpan MaxBlocksResponseDuration = TimeSpan.FromSeconds(60);

    protected BeaconChainSpec Spec { get; } = spec;

    /// <summary>The context bytes of a block chunk: the fork digest of the block's slot epoch.</summary>
    protected byte[] ContextBytesFor(SignedBeaconBlock block) => ForkDigest.Compute(Spec, Spec.GetEpoch(block.Message!.Slot));

    /// <param name="overallTimeout">Overrides <see cref="MaxBlocksResponseDuration"/>; test-only seam, production call sites omit it.</param>
    protected async Task<IReadOnlyList<SignedBeaconBlock>> ReadBlockChunksAsync(Stream stream, int maxBlocks, string protocolId, TimeSpan? overallTimeout = null)
    {
        List<SignedBeaconBlock> blocks = [];
        using BoundedTimeout timeout = StartBoundedTimeout(TtfbTimeout + RespTimeout, overallTimeout ?? MaxBlocksResponseDuration);
        CancellationTokenSource cts = timeout.Cts;
        try
        {
            while (await ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, cts.Token) is { } chunk)
            {
                if (chunk.Result != ReqRespFraming.ResponseCode.Success)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.PeerError);
                    throw ErrorChunkToException(chunk);
                }

                if (blocks.Count >= maxBlocks)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.LimitExceeded);
                    throw new Eth2ReqRespException($"Peer responded with more than the requested {maxBlocks} blocks");
                }

                SignedBeaconBlock block;
                try
                {
                    SignedBeaconBlock.Decode(chunk.Payload, out block);
                }
                catch (Exception e) when (e is not Eth2ReqRespException and not OperationCanceledException)
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException($"Malformed block chunk: {e.Message}");
                }

                if (!chunk.ContextBytes.AsSpan().SequenceEqual(ContextBytesFor(block)))
                {
                    RecordFailure(protocolId, ReqRespFailureReason.InvalidMessage);
                    throw new Eth2ReqRespException($"Block chunk context bytes do not match the fork digest of slot {block.Message!.Slot}");
                }

                blocks.Add(block);
                cts.CancelAfter(RespTimeout);
            }
        }
        catch (OperationCanceledException)
        {
            RecordFailure(protocolId, ReqRespFailureReason.Timeout);
            throw;
        }

        return blocks;
    }

    protected Task WriteBlockChunkAsync(Stream stream, SignedBeaconBlock block, CancellationTokenSource cts)
    {
        cts.CancelAfter(RespTimeout);
        return ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, ContextBytesFor(block), SignedBeaconBlock.Encode(block), cts.Token);
    }
}
