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

    /// <summary>The phase0 <c>compute_min_epochs_for_block_requests</c>, in force until Gloas replaces it (phase0 p2p-interface.md).</summary>
    private static readonly ulong MinEpochsForBlockRequestsBeforeGloas = Presets.MinValidatorWithdrawabilityDelay + Presets.ChurnLimitQuotient / 2;

    public string Id => "/eth2/beacon_chain/req/beacon_blocks_by_head/1/ssz_snappy";

    public Task DialAsync(IChannel downChannel, ISessionContext context) => throw new NotSupportedException("Use block range or root requests for outbound sync");

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        Stream stream = new ChannelStreamAdapter(downChannel);
        await using InboundRequest? inbound = TryEnterInbound(context, Id);
        if (inbound is null)
        {
            using CancellationTokenSource refusal = StartTimeout(TtfbTimeout);
            await ReqRespFraming.WriteErrorChunkAsync(stream, ReqRespFraming.ResponseCode.RateLimited, "Too many concurrent requests", refusal.Token);
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
            ulong servedEpochs = currentEpoch >= Spec.GloasForkEpoch ? BeaconChainStore.MinEpochsForBlockRequests : MinEpochsForBlockRequestsBeforeGloas;
            ulong minimumEpoch = currentEpoch > servedEpochs ? currentEpoch - servedEpochs : 0;
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
            RecordRefusal(Id, e.ResponseCode);
            await ReqRespFraming.WriteErrorChunkAsync(stream, e.ResponseCode, e.Message, cts.Token);
        }
        catch (OperationCanceledException)
        {
            RecordFailure(Id, ReqRespFailureReason.Timeout);
            throw;
        }
    }
}
