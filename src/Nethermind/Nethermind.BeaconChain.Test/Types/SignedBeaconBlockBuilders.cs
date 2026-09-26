// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Test.Types;

/// <summary>Minimal signed beacon blocks of the Fulu and Gloas shapes, and the Sepolia fork boundary between them.</summary>
internal static class SignedBeaconBlockBuilders
{
    /// <summary>The only network with a scheduled Gloas fork, so both shapes occur on it.</summary>
    public static readonly BeaconChainSpec Sepolia = BeaconChainSpec.Sepolia;

    /// <summary>The first Sepolia slot whose block has the Gloas shape; the slot before it is the last Fulu slot.</summary>
    public static readonly ulong FirstGloasSlot = Sepolia.GloasForkEpoch * Sepolia.SlotsPerEpoch;

    /// <summary>A Gloas-shaped block at <paramref name="slot"/> with empty lists and a self-built bid.</summary>
    /// <param name="slot">The block's slot.</param>
    /// <param name="parentRoot">The block's <c>parent_root</c>; zero when omitted.</param>
    /// <param name="bidParentRoot">The bid's <c>parent_block_root</c>; <paramref name="parentRoot"/> when omitted, as <c>process_execution_payload_bid</c> requires.</param>
    public static SignedBeaconBlockGloas CreateMinimalGloasBlock(ulong slot, Hash256? parentRoot = null, Hash256? bidParentRoot = null) => new()
    {
        Message = new BeaconBlockGloas
        {
            Slot = slot,
            ProposerIndex = 21,
            ParentRoot = parentRoot ?? Hash256.Zero,
            StateRoot = Hash256.Zero,
            Body = new BeaconBlockBodyGloas
            {
                Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
                Graffiti = Hash256.Zero,
                ProposerSlashings = [],
                AttesterSlashings = [],
                Attestations = [],
                Deposits = [],
                VoluntaryExits = [],
                SyncAggregate = new SyncAggregate { SyncCommitteeBits = new BitArray(Presets.SyncCommitteeSize) },
                BlsToExecutionChanges = [],
                SignedExecutionPayloadBid = new SignedExecutionPayloadBid
                {
                    Message = new ExecutionPayloadBid
                    {
                        ParentBlockHash = Hash256.Zero,
                        ParentBlockRoot = bidParentRoot ?? parentRoot ?? Hash256.Zero,
                        BlockHash = Hash256.Zero,
                        PrevRandao = Hash256.Zero,
                        FeeRecipient = Address.Zero,
                        GasLimit = 30_000_000,
                        BuilderIndex = Presets.BuilderIndexSelfBuild,
                        Slot = slot,
                        BlobKzgCommitments = [],
                        ExecutionRequestsRoot = Hash256.Zero,
                    },
                },
                PayloadAttestations = [],
                ParentExecutionRequests = new ExecutionRequestsGloas(),
            },
        },
    };

    /// <summary>A Fulu-shaped block at <paramref name="slot"/> with empty lists.</summary>
    public static SignedBeaconBlock CreateMinimalBlock(ulong slot) => new()
    {
        Message = new BeaconBlock
        {
            Slot = slot,
            ProposerIndex = 21,
            ParentRoot = Hash256.Zero,
            StateRoot = Hash256.Zero,
            Body = new BeaconBlockBody
            {
                Eth1Data = new Eth1Data { DepositRoot = Hash256.Zero, DepositCount = 0, BlockHash = Hash256.Zero },
                Graffiti = Hash256.Zero,
                ProposerSlashings = [],
                AttesterSlashings = [],
                Attestations = [],
                Deposits = [],
                VoluntaryExits = [],
                SyncAggregate = new SyncAggregate { SyncCommitteeBits = new BitArray(512) },
                ExecutionPayload = new ExecutionPayload
                {
                    ParentHash = Hash256.Zero,
                    FeeRecipient = Address.Zero,
                    StateRoot = Hash256.Zero,
                    ReceiptsRoot = Hash256.Zero,
                    LogsBloom = Bloom.Empty,
                    PrevRandao = Hash256.Zero,
                    BlockNumber = 23_000_000,
                    GasLimit = 30_000_000,
                    GasUsed = 21_000,
                    Timestamp = 1_750_000_000,
                    ExtraData = Bytes.FromHexString("0xc0ffee"),
                    BaseFeePerGas = 7,
                    BlockHash = Hash256.Zero,
                    Transactions = [],
                    Withdrawals = [],
                    BlobGasUsed = 0,
                    ExcessBlobGas = 0,
                },
                BlsToExecutionChanges = [],
                BlobKzgCommitments = [],
                ExecutionRequests = new ExecutionRequests { Deposits = [], Withdrawals = [], Consolidations = [] },
            },
        },
    };
}
