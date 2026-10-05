// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Test.Types;

internal static class SignedBeaconBlockBuilders
{
    public static readonly BeaconChainSpec Sepolia = BeaconChainSpec.Sepolia;

    public static readonly ulong FirstGloasSlot = Sepolia.GloasForkEpoch * Sepolia.SlotsPerEpoch;

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
