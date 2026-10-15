// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;

namespace Nethermind.BeaconChain.P2P.ReqResp.Protocols;

/// <summary>Serves a block and its ancestors in descending slot order (Fulu p2p-interface.md).</summary>
public sealed class BeaconBlocksByHeadProtocolV1(BeaconChainSpec spec, BeaconChainStore store, SlotClock? clock = null) : BlocksProtocolBase(spec), ISessionProtocol
{
    private const int RequestLength = Hash256.Size + sizeof(ulong);
    public string Id => "/eth2/beacon_chain/req/beacon_blocks_by_head/1/ssz_snappy";

    public Task DialAsync(IChannel downChannel, ISessionContext context) => throw new NotSupportedException("Use block range or root requests for outbound sync");

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        await using InboundRequest? inbound = TryEnterInbound(context, Id);
        if (inbound is null)
        {
            using CancellationTokenSource refusal = StartTimeout(TtfbTimeout);
            await ReqRespFraming.WriteErrorChunkAsync(stream, 139, "Too many concurrent requests", refusal.Token);
            return;
        }

        using BoundedTimeout timeout = StartBoundedTimeout(RespTimeout, MaxBlocksResponseDuration);
        CancellationTokenSource cts = timeout.Cts;
        try
        {
            byte[] request = await inbound.ReadRequestAsync(stream, RequestLength, cts.Token);
            if (request.Length != RequestLength) throw new Eth2ReqRespException("Blocks-by-head request must contain a root and count");
            Hash256 root = new(request.AsSpan(0, Hash256.Size));
            ulong count = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(Hash256.Size));
            if (count == 0 || count > MaxRequestBlocks) throw new Eth2ReqRespException("Blocks-by-head count must be between 1 and 128");
            ChargeRequest(context, count, MaxRequestBlocks);
            ulong currentEpoch = clock?.CurrentEpoch ?? 0;
            ulong minimumEpoch = currentEpoch > BeaconChainStore.MinEpochsForBlockRequests ? currentEpoch - BeaconChainStore.MinEpochsForBlockRequests : 0;
            ulong previousSlot = ulong.MaxValue;
            for (ulong i = 0; i < count && store.TryGetForkedBlock(root, out ForkedSignedBeaconBlock? block); i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (Spec.GetEpoch(block.Slot) < minimumEpoch) break;
                if (i > 0 && block.Slot >= previousSlot) break;
                await WriteBlockChunkAsync(stream, block, cts);
                previousSlot = block.Slot;
                root = block.ParentRoot;
                if (block.Slot == 0) break;
            }
        }
        catch (Eth2ReqRespException e)
        {
            if (e.ResponseCode == ReqRespFraming.ResponseCode.InvalidRequest) RecordFailure(Id, ReqRespFailureReason.InvalidMessage);
            await ReqRespFraming.WriteErrorChunkAsync(stream, e.ResponseCode, e.Message, cts.Token);
        }
        catch (OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.Timeout);
            throw;
        }
    }
}
