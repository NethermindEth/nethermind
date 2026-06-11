// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Storage;

public class BeaconChainStoreTests
{
    [Test]
    public void Round_trips_blocks_canonical_index_chunked_states_metadata_and_anchor()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        Hash256 blockRoot = new(Bytes.FromHexString("0x1111111111111111111111111111111111111111111111111111111111111111"));
        Hash256 missingRoot = new(Bytes.FromHexString("0x2222222222222222222222222222222222222222222222222222222222222222"));

        SignedBeaconBlock block = CreateMinimalBlock(12_345_678);
        store.PutBlock(blockRoot, block);

        store.SetCanonicalRoot(12_345_678, blockRoot);

        // ~10 MB forces three 4 MB chunks; random data also exercises barely-compressible chunks.
        byte[] stateSsz = new byte[10 * 1024 * 1024 + 17];
        new Random(42).NextBytes(stateSsz);
        store.PutState(blockRoot, stateSsz);

        store.PutMetadata("schemaVersion", [1]);
        store.SetAnchor(blockRoot, 12_345_678);

        Assert.Multiple(() =>
        {
            Assert.That(store.TryGetBlock(blockRoot, out SignedBeaconBlock? readBlock), Is.True);
            Assert.That(SignedBeaconBlock.Encode(readBlock!), Is.EqualTo(SignedBeaconBlock.Encode(block)));
            Assert.That(store.TryGetBlock(missingRoot, out _), Is.False);

            Assert.That(store.TryGetCanonicalRoot(12_345_678, out Hash256? canonicalRoot), Is.True);
            Assert.That(canonicalRoot, Is.EqualTo(blockRoot));
            Assert.That(store.TryGetCanonicalRoot(1, out _), Is.False);

            Assert.That(store.TryGetState(blockRoot, out byte[]? readState), Is.True);
            Assert.That(readState, Is.EqualTo(stateSsz));
            Assert.That(store.TryGetState(missingRoot, out _), Is.False);

            Assert.That(store.GetMetadata("schemaVersion"), Is.EqualTo(new byte[] { 1 }));
            Assert.That(store.GetMetadata("missing"), Is.Null);

            Assert.That(store.TryGetAnchor(out Hash256? anchorRoot, out ulong anchorSlot), Is.True);
            Assert.That(anchorRoot, Is.EqualTo(blockRoot));
            Assert.That(anchorSlot, Is.EqualTo(12_345_678ul));
        });
    }

    private static SignedBeaconBlock CreateMinimalBlock(ulong slot) => new()
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
