// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
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
        using Context context = new();
        IIndexProofRpcModule module = context.Module;

        ResultWrapper<IndexProofResult?> result = module.indexProof_getTransactionProof(TestItem.KeccakA, 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            Assert.That(result.Data, Is.Null);
        }
    }

    [Test]
    public void GetTransactionProof_tx_not_found_returns_resource_not_found()
    {
        using Context context = new();
        IndexEntry entry = IndexEntry.CreateBlock(TestItem.KeccakA, 100);
        context.Store(0, 100, [entry]);

        IIndexProofRpcModule module = context.Module;
        ResultWrapper<IndexProofResult?> result = module.indexProof_getTransactionProof(TestItem.KeccakB, 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            Assert.That(result.Data, Is.Null);
        }
    }

    [Test]
    public void GetTransactionProof_success_returns_valid_proof()
    {
        using Context context = new();
        Hash256 txHash = TestItem.KeccakA;
        IndexEntry txEntry = IndexEntry.CreateTransaction(txHash, 100, 0, 0);
        IndexEntry blockEntry = IndexEntry.CreateBlock(TestItem.KeccakB, 100);
        List<IndexEntry> entries = [blockEntry, txEntry];
        entries.Sort();

        context.Store(0, 100, entries);

        IIndexProofRpcModule module = context.Module;
        ResultWrapper<IndexProofResult?> result = module.indexProof_getTransactionProof(txHash, 100);

        Assert.That(result.Data, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            IndexProofResult proof = result.Data!;
            Assert.That(proof.FirstBlock, Is.EqualTo(100));
            Assert.That(proof.Level, Is.EqualTo(0));
            Assert.That(proof.TableSize, Is.EqualTo(1));
            Assert.That(proof.ListLength, Is.EqualTo(2));
            Assert.That(proof.Proof.Length, Is.GreaterThan(0));
            Assert.That(proof.StorageSlot, Does.StartWith("0x"));
        }
    }

    [Test]
    public void GetLogAddressProofs_table_not_found_returns_resource_not_found()
    {
        using Context context = new();
        IIndexProofRpcModule module = context.Module;

        ResultWrapper<IndexProofResult[]?> result = module.indexProof_getLogAddressProofs(TestItem.AddressA, 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            Assert.That(result.Data, Is.Null);
        }
    }

    [Test]
    public void GetLogAddressProofs_no_matching_address_returns_empty_array()
    {
        using Context context = new();
        IndexEntry entry = IndexEntry.CreateLogAddress(TestItem.AddressA, 100, 0, 0);
        context.Store(0, 100, [entry]);

        IIndexProofRpcModule module = context.Module;
        ResultWrapper<IndexProofResult[]?> result = module.indexProof_getLogAddressProofs(TestItem.AddressB, 100);

        Assert.That(result.Data, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data!.Length, Is.EqualTo(0));
        }
    }

    [Test]
    public void GetLogAddressProofs_multiple_matching_logs_returns_all_proofs([Values(0, 1, 2)] int targetIndex)
    {
        using Context context = new();
        Address[] addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC];
        List<IndexEntry> entries = [IndexEntry.CreateBlock(TestItem.KeccakA, 100), IndexEntry.CreateTransaction(TestItem.KeccakB, 100, 0, 0)];
        foreach (Address address in addresses)
        {
            entries.Add(IndexEntry.CreateLogAddress(address, 100, 0, 0));
            entries.Add(IndexEntry.CreateLogAddress(address, 100, 1, 0));
        }

        entries.Sort();

        context.Store(0, 100, entries);

        Address targetAddress = addresses[targetIndex];
        IIndexProofRpcModule module = context.Module;
        ResultWrapper<IndexProofResult[]?> result = module.indexProof_getLogAddressProofs(targetAddress, 100);

        Assert.That(result.Data, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data!.Length, Is.EqualTo(2));
            foreach (IndexProofResult proof in result.Data!)
            {
                Assert.That(entries[proof.LeafIndex].Type, Is.EqualTo(IndexEntryType.LogAddress));
                Assert.That(entries[proof.LeafIndex].ContentBytes.ToArray(), Is.EqualTo(targetAddress.Bytes.ToArray()));
            }
        }
    }

    [Test]
    public void GetStorageSlot_invalid_level_fails()
    {
        using Context context = new();
        IIndexProofRpcModule module = context.Module;

        ResultWrapper<StorageSlotInfo> result = module.indexProof_getStorageSlot(10, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(result.Data, Is.Null);
        }
    }

    [TestCase(0, 0, ExpectedResult = "0x400")]   // 1*1024 + 0 = 1024 = 0x400
    [TestCase(1, 0, ExpectedResult = "0x1000")]  // 4*1024 + 0 = 4096 = 0x1000
    [TestCase(2, 0, ExpectedResult = "0x4000")]  // 16*1024 + 0 = 16384 = 0x4000
    public string GetStorageSlot_valid_params_returns_correct_slot(int level, long firstBlock)
    {
        using Context context = new();
        IIndexProofRpcModule module = context.Module;

        ResultWrapper<StorageSlotInfo> result = module.indexProof_getStorageSlot(level, firstBlock);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
        return result.Data!.Slot;
    }

    [Test]
    public void GetTransactionProof_with_higher_level_table_aligns_first_block_and_returns_proof()
    {
        using Context context = new();
        Hash256 txHash = TestItem.KeccakA;
        IndexEntry txEntry = IndexEntry.CreateTransaction(txHash, 6, 0, 0);
        IndexEntry blockEntry = IndexEntry.CreateBlock(TestItem.KeccakB, 5);
        List<IndexEntry> entries = [blockEntry, txEntry];
        entries.Sort();

        // Store at level 1 (table size 4), firstBlock = 4 (covers blocks 4..7)
        context.Store(1, 4, entries);

        IIndexProofRpcModule module = context.Module;
        // Query with blockNumber = 6 within the table range [4..7]
        ResultWrapper<IndexProofResult?> result = module.indexProof_getTransactionProof(txHash, blockNumber: 6, level: 1);

        Assert.That(result.Data, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            IndexProofResult proof = result.Data!;
            Assert.That(proof.FirstBlock, Is.EqualTo(4));
            Assert.That(proof.Level, Is.EqualTo(1));
            Assert.That(proof.TableSize, Is.EqualTo(4));
            Assert.That(proof.Proof.Length, Is.GreaterThan(0));
        }
    }

    [Test]
    public void GetTransactionProof_negative_block_or_invalid_level_fails()
    {
        using Context context = new();
        IIndexProofRpcModule module = context.Module;

        ResultWrapper<IndexProofResult?> negativeBlock = module.indexProof_getTransactionProof(TestItem.KeccakA, -1, 0);
        ResultWrapper<IndexProofResult?> invalidLevel = module.indexProof_getTransactionProof(TestItem.KeccakA, 10, 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(negativeBlock.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(invalidLevel.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
        }
    }

    [Test]
    public void GetStorageSlot_negative_block_fails()
    {
        using Context context = new();
        IIndexProofRpcModule module = context.Module;

        ResultWrapper<StorageSlotInfo> result = module.indexProof_getStorageSlot(0, -1);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [Test]
    public void ProofEndpoints_resolve_against_canonical_branch_when_side_branch_was_cached_afterwards()
    {
        using Context context = new();
        Hash256 canonicalHash = context.BlockTree.FindHeader(100, BlockTreeLookupOptions.RequireCanonical)!.Hash!;
        Hash256 sideBranchHash = TestItem.KeccakB;

        Hash256 canonicalTxHash = TestItem.KeccakC;
        Hash256 sideBranchTxHash = TestItem.KeccakD;

        IndexEntry canonicalTx = IndexEntry.CreateTransaction(canonicalTxHash, 100, 0, 0);
        IndexEntry sideBranchTx = IndexEntry.CreateTransaction(sideBranchTxHash, 100, 0, 0);

        context.Store(0, 100, [canonicalTx], canonicalHash);
        context.Store(0, 100, [sideBranchTx], sideBranchHash);

        IIndexProofRpcModule module = context.Module;

        ResultWrapper<IndexProofResult?> canonicalResult = module.indexProof_getTransactionProof(canonicalTxHash, 100);
        ResultWrapper<IndexProofResult?> sideBranchResult = module.indexProof_getTransactionProof(sideBranchTxHash, 100);

        Assert.That(canonicalResult.Data, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(canonicalResult.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(canonicalResult.Data!.FirstBlock, Is.EqualTo(100));
            Assert.That(sideBranchResult.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
        }
    }

    [Test]
    public void ProofEndpoints_return_not_found_when_the_publication_block_is_not_canonical()
    {
        using Context context = new();
        Hash256 txHash = TestItem.KeccakC;
        context.Store(0, 100, [IndexEntry.CreateTransaction(txHash, 100, 0, 0)], TestItem.KeccakB);

        IIndexProofRpcModule module = context.Module;

        ResultWrapper<IndexProofResult?> result = module.indexProof_getTransactionProof(txHash, 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
            Assert.That(result.Data, Is.Null);
        }
    }

    private sealed class Context : IDisposable
    {
        private readonly IContainer _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(101).TestObject)
            .Build();

        public IIndexProofRpcModule Module => _container.Resolve<IIndexProofRpcModule>();

        public IBlockTree BlockTree => _container.Resolve<IBlockTree>();

        /// <summary>Stores a table under <paramref name="blockHash"/>, by default the canonical hash of its publication block.</summary>
        public void Store(int level, long firstBlock, IReadOnlyList<IndexEntry> sortedEntries, Hash256? blockHash = null)
        {
            long publicationBlock = level == 0 ? firstBlock : IndexTableMergeScheduler.PublicationBlock(level, firstBlock);
            blockHash ??= BlockTree.FindHeader((ulong)publicationBlock, BlockTreeLookupOptions.RequireCanonical)!.Hash!;
            _container.Resolve<IndexTableStore>().Store(level, firstBlock, sortedEntries, blockHash);
        }

        public void Dispose() => _container.Dispose();
    }
}
