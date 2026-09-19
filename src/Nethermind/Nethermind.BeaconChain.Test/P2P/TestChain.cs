// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>Builds parent-linked minimal block chains for the P2P tests.</summary>
internal static class TestChain
{
    /// <summary>Creates an anchor block at <paramref name="anchorSlot"/> and parent-linked blocks at each of <paramref name="slots"/>.</summary>
    public static (SignedBeaconBlock Anchor, Hash256 AnchorRoot, SignedBeaconBlock[] Blocks) BuildLinkedChain(ulong anchorSlot, params ulong[] slots)
    {
        SignedBeaconBlock anchor = CreateBlock(anchorSlot, Hash256.Zero);
        Hash256 parentRoot = SszRoots.HashTreeRoot(anchor.Message!);
        Hash256 anchorRoot = parentRoot;

        SignedBeaconBlock[] blocks = new SignedBeaconBlock[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            blocks[i] = CreateBlock(slots[i], parentRoot);
            parentRoot = SszRoots.HashTreeRoot(blocks[i].Message!);
        }

        return (anchor, anchorRoot, blocks);
    }

    /// <summary>Persists the chain as the canonical anchored history of <paramref name="store"/>.</summary>
    public static void Persist(BeaconChainStore store, SignedBeaconBlock anchor, Hash256 anchorRoot, IEnumerable<SignedBeaconBlock> blocks)
    {
        store.SetAnchor(anchorRoot, anchor.Message!.Slot);
        store.PutBlock(anchorRoot, anchor);
        store.SetCanonicalRoot(anchor.Message.Slot, anchorRoot);
        foreach (SignedBeaconBlock block in blocks)
        {
            Hash256 root = SszRoots.HashTreeRoot(block.Message!);
            store.PutBlock(root, block);
            store.SetCanonicalRoot(block.Message!.Slot, root);
        }
    }

    public static SignedBeaconBlock CreateBlock(ulong slot, Hash256 parentRoot) => new()
    {
        Message = new BeaconBlock
        {
            Slot = slot,
            ProposerIndex = 21,
            ParentRoot = parentRoot,
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
                    BlockNumber = slot,
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
