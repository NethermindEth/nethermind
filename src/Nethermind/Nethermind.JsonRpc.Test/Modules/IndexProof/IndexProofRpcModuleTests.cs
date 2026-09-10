// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.IndexProof;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.IndexProof;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class IndexProofRpcModuleTests
{
    [Test]
    public void GetTransactionProof_table_not_found_returns_resource_not_found()
    {
        IndexTableStore store = new();
        IndexProofRpcModule module = new(store);

        ResultWrapper<IndexProofResult?> result = module.indexProof_getTransactionProof(TestItem.KeccakA, 100);

        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            Assert.That(result.Data, Is.Null);
        });
    }

    [Test]
    public void GetTransactionProof_tx_not_found_returns_resource_not_found()
    {
        IndexTableStore store = new();
        IndexEntry entry = IndexEntry.CreateBlock(TestItem.KeccakA, 100);
        store.Store(0, 100, [entry]);

        IndexProofRpcModule module = new(store);
        ResultWrapper<IndexProofResult?> result = module.indexProof_getTransactionProof(TestItem.KeccakB, 100);

        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            Assert.That(result.Data, Is.Null);
        });
    }

    [Test]
    public void GetTransactionProof_success_returns_valid_proof()
    {
        IndexTableStore store = new();
        Hash256 txHash = TestItem.KeccakA;
        IndexEntry txEntry = IndexEntry.CreateTransaction(txHash, 100, 0, 0);
        IndexEntry blockEntry = IndexEntry.CreateBlock(TestItem.KeccakB, 100);
        List<IndexEntry> entries = [blockEntry, txEntry];
        entries.Sort();

        store.Store(0, 100, entries);

        IndexProofRpcModule module = new(store);
        ResultWrapper<IndexProofResult?> result = module.indexProof_getTransactionProof(txHash, 100);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data, Is.Not.Null);
            IndexProofResult proof = result.Data!;
            Assert.That(proof.FirstBlock, Is.EqualTo(100));
            Assert.That(proof.Level, Is.EqualTo(0));
            Assert.That(proof.TableSize, Is.EqualTo(1));
            Assert.That(proof.ListLength, Is.EqualTo(2));
            Assert.That(proof.Proof.Length, Is.GreaterThan(0));
            Assert.That(proof.StorageSlot, Does.StartWith("0x"));
        });
    }

    [Test]
    public void GetLogAddressProofs_table_not_found_returns_resource_not_found()
    {
        IndexTableStore store = new();
        IndexProofRpcModule module = new(store);

        ResultWrapper<IndexProofResult[]?> result = module.indexProof_getLogAddressProofs(TestItem.AddressA, 100);

        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            Assert.That(result.Data, Is.Null);
        });
    }

    [Test]
    public void GetLogAddressProofs_no_matching_address_returns_empty_array()
    {
        IndexTableStore store = new();
        IndexEntry entry = IndexEntry.CreateLogAddress(TestItem.AddressA, 100, 0, 0);
        store.Store(0, 100, [entry]);

        IndexProofRpcModule module = new(store);
        ResultWrapper<IndexProofResult[]?> result = module.indexProof_getLogAddressProofs(TestItem.AddressB, 100);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data, Is.Not.Null);
            Assert.That(result.Data!.Length, Is.EqualTo(0));
        });
    }

    [Test]
    public void GetLogAddressProofs_multiple_matching_logs_returns_all_proofs()
    {
        IndexTableStore store = new();
        Address targetAddress = TestItem.AddressA;
        IndexEntry log1 = IndexEntry.CreateLogAddress(targetAddress, 100, 0, 0);
        IndexEntry log2 = IndexEntry.CreateLogAddress(targetAddress, 100, 1, 0);
        IndexEntry otherLog = IndexEntry.CreateLogAddress(TestItem.AddressB, 100, 0, 0);
        List<IndexEntry> entries = [log1, otherLog, log2];
        entries.Sort();

        store.Store(0, 100, entries);

        IndexProofRpcModule module = new(store);
        ResultWrapper<IndexProofResult[]?> result = module.indexProof_getLogAddressProofs(targetAddress, 100);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data, Is.Not.Null);
            Assert.That(result.Data!.Length, Is.EqualTo(2));
        });
    }

    [Test]
    public void GetStorageSlot_invalid_level_fails()
    {
        IndexTableStore store = new();
        IndexProofRpcModule module = new(store);

        ResultWrapper<StorageSlotInfo> result = module.indexProof_getStorageSlot(10, 0);

        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(result.Data, Is.Null);
        });
    }

    [TestCase(0, 0, ExpectedResult = "0x400")]   // 1*1024 + 0 = 1024 = 0x400
    [TestCase(1, 0, ExpectedResult = "0x1000")]  // 4*1024 + 0 = 4096 = 0x1000
    [TestCase(2, 0, ExpectedResult = "0x4000")]  // 16*1024 + 0 = 16384 = 0x4000
    public string GetStorageSlot_valid_params_returns_correct_slot(int level, long firstBlock)
    {
        IndexTableStore store = new();
        IndexProofRpcModule module = new(store);

        ResultWrapper<StorageSlotInfo> result = module.indexProof_getStorageSlot(level, firstBlock);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        return result.Data!.Slot;
    }

    [Test]
    public void GetTransactionProof_with_higher_level_table_aligns_first_block_and_returns_proof()
    {
        IndexTableStore store = new();
        Hash256 txHash = TestItem.KeccakA;
        IndexEntry txEntry = IndexEntry.CreateTransaction(txHash, 6, 0, 0);
        IndexEntry blockEntry = IndexEntry.CreateBlock(TestItem.KeccakB, 5);
        List<IndexEntry> entries = [blockEntry, txEntry];
        entries.Sort();

        // Store at level 1 (table size 4), firstBlock = 4 (covers blocks 4..7)
        store.Store(1, 4, entries);

        IndexProofRpcModule module = new(store);
        // Query with blockNumber = 6 within the table range [4..7]
        ResultWrapper<IndexProofResult?> result = module.indexProof_getTransactionProof(txHash, blockNumber: 6, level: 1);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data, Is.Not.Null);
            IndexProofResult proof = result.Data!;
            Assert.That(proof.FirstBlock, Is.EqualTo(4));
            Assert.That(proof.Level, Is.EqualTo(1));
            Assert.That(proof.TableSize, Is.EqualTo(4));
            Assert.That(proof.Proof.Length, Is.GreaterThan(0));
        });
    }

    [Test]
    public void GetTransactionProof_negative_block_or_invalid_level_fails()
    {
        IndexTableStore store = new();
        IndexProofRpcModule module = new(store);

        ResultWrapper<IndexProofResult?> negativeBlock = module.indexProof_getTransactionProof(TestItem.KeccakA, -1, 0);
        ResultWrapper<IndexProofResult?> invalidLevel = module.indexProof_getTransactionProof(TestItem.KeccakA, 10, 5);

        Assert.Multiple(() =>
        {
            Assert.That(negativeBlock.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(invalidLevel.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
        });
    }

    [Test]
    public void GetStorageSlot_negative_block_fails()
    {
        IndexTableStore store = new();
        IndexProofRpcModule module = new(store);

        ResultWrapper<StorageSlotInfo> result = module.indexProof_getStorageSlot(0, -1);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
    }
}
