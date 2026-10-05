// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>The eth2 <c>beacon_blocks_by_range</c> v2 protocol.</summary>
/// <remarks>
/// The listen side serves canonical blocks from the local <see cref="BeaconChainStore"/>, skipping
/// empty slots, and answers <c>ResourceUnavailable</c> for ranges older than the earliest stored block.
/// The dial side validates per-chunk fork-digest context bytes and strictly increasing slots within
/// the requested range.
/// </remarks>
public sealed class BeaconBlocksByRangeProtocolV2(BeaconChainSpec spec, BeaconChainStore store) : BlocksProtocolBase(spec),
    ISessionProtocol<BeaconBlocksByRangeDial, IReadOnlyList<ForkedSignedBeaconBlock>>
{
    private const int RequestLength = 3 * sizeof(ulong);

    public string Id => "/eth2/beacon_chain/req/beacon_blocks_by_range/2/ssz_snappy";

    public async Task<IReadOnlyList<ForkedSignedBeaconBlock>> DialAsync(IChannel downChannel, ISessionContext context, BeaconBlocksByRangeDial dial)
    {
        BeaconBlocksByRangeRequest request = dial.Request;
        using RequestTiming.Exchange exchange = RequestTiming.Open(request);
        RequestTiming? timing = exchange.Timing;
        Stream stream = new ChannelStreamAdapter(downChannel);
        await WriteRequestAndEofAsync(downChannel, stream, BeaconBlocksByRangeRequest.Encode(request), RespTimeout);

        // Checked per chunk, so every block handed to OnBlock is inside the requested range and in slot order.
        ulong? previousSlot = null;
        return await ReadBlockChunksAsync(stream, (int)Math.Min(request.Count, MaxRequestBlocks), Id, timing: timing, onBlock: block =>
        {
            ulong slot = block.Slot;
            if (slot < request.StartSlot || slot >= request.StartSlot + request.Count || slot <= previousSlot)
            {
                throw new Eth2ReqRespException($"Block slot {slot} outside the requested range or out of order");
            }

            previousSlot = slot;
            dial.OnBlock?.Invoke(block);
        });
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        await using InboundRequest? inboundSlot = TryEnterInbound(context, Id);
        if (inboundSlot is null)
        {
            return;
        }

        using BoundedTimeout timeout = StartBoundedTimeout(RespTimeout, MaxBlocksResponseDuration);
        CancellationTokenSource cts = timeout.Cts;
        try
        {
            byte[] requestSsz = await inboundSlot.ReadRequestAsync(stream, RequestLength, cts.Token);
            if (requestSsz.Length != RequestLength)
            {
                throw new Eth2ReqRespException($"Blocks-by-range request must be {RequestLength} bytes, got {requestSsz.Length}");
            }

            BeaconBlocksByRangeRequest.Decode(requestSsz, out BeaconBlocksByRangeRequest request);
            if (request.Count == 0)
            {
                throw new Eth2ReqRespException("Blocks-by-range request count must be positive");
            }

            // BeaconBlocksByRange: a peer unable to reply SHOULD answer ResourceUnavailable; an empty reply would claim the slots are empty.
            if (!store.TryGetEarliestBlockSlot(out ulong earliestSlot) || request.StartSlot < Math.Min(earliestSlot, store.BackfilledBlockFloor ?? earliestSlot))
            {
                throw new Eth2ReqRespException("Requested range predates the earliest stored block", ReqRespFraming.ResponseCode.ResourceUnavailable);
            }

            // Per the spec, step is deprecated and the request is served as if it were 1.
            ulong count = Math.Min(request.Count, MaxRequestBlocks);
            Hash256? previousRoot = null;
            for (ulong offset = 0; offset < count && offset <= ulong.MaxValue - request.StartSlot; offset++)
            {
                ulong slot = request.StartSlot + offset;
                if (store.TryGetCanonicalRoot(slot, out Hash256? root) && store.TryGetForkedBlock(root, out ForkedSignedBeaconBlock? block))
                {
                    // BeaconBlocksByRange: each parent_root MUST match the preceding block, so a reorg mid-reply ends the reply.
                    if (previousRoot is not null && block.ParentRoot != previousRoot)
                    {
                        break;
                    }

                    await WriteBlockChunkAsync(stream, block, cts);
                    previousRoot = root;
                }
            }
        }
        catch (Eth2ReqRespException e)
        {
            if (e.ResponseCode != ReqRespFraming.ResponseCode.ResourceUnavailable)
            {
                RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
            }

            await ReqRespFraming.WriteErrorChunkAsync(stream, e.ResponseCode, e.Message, cts.Token);
        }
        catch (OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.Timeout);
        }
    }
}

/// <summary>A <c>beacon_blocks_by_range</c> dial: the wire request and a receiver for each block as it is read.</summary>
/// <param name="OnBlock">Receives each block inside the requested range and in slot order as it is read, so a reply that later fails still leaves what it delivered.</param>
public readonly record struct BeaconBlocksByRangeDial(BeaconBlocksByRangeRequest Request, Action<ForkedSignedBeaconBlock>? OnBlock = null);

/// <summary>A by-range block request that failed after delivering some blocks; carries them so the caller keeps what arrived.</summary>
/// <remarks>The message is the cause's, unwrapped only from an aggregate, so failure classification by text is unchanged.</remarks>
internal sealed class PartialBlocksException(Exception cause, IReadOnlyList<ForkedSignedBeaconBlock> received)
    : Exception(((cause as AggregateException)?.Flatten().InnerException ?? cause).Message, cause)
{
    /// <summary>The blocks read before the failure: inside the requested range and in slot order, not yet checked for parent linkage.</summary>
    public IReadOnlyList<ForkedSignedBeaconBlock> Received { get; } = received;
}
