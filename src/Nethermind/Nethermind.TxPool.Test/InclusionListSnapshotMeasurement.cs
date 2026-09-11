// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Spec;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.TxPool.Collections;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>
/// Times the pool snapshot behind <c>engine_getInclusionListV1</c> against a trie-backed state reader, for a pool
/// of ordinary transactions and for one filled with EIP-8250 keyed transactions at the maximum key count.
/// </summary>
/// <remarks>
/// Reports <c>RESULT</c> lines for three snapshots of the same pool: <c>filtered</c> is the full readiness filter,
/// <c>nonframe</c> the inclusion list's, and <c>unfiltered</c> the bucket copy alone, which separates the copy
/// from the readiness reads. The whole snapshot runs under the pool's exclusive lock.
///
/// The state lives in a <c>MemDb</c>, so every read here is a warm in-process trie traversal — a floor for what
/// the same scan costs against RocksDB.
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
[NonParallelizable]
public class InclusionListSnapshotMeasurement
{
    private const int Warmup = 5;
    private const int Samples = 30;
    private const ulong KeyedSeq = 1;

    private static readonly ISpecProvider SpecProvider = MainnetSpecProvider.Instance;

    [TestCase(2048, false, TestName = "ordinary transactions at the default pool size")]
    [TestCase(2048, true, TestName = "keyed transactions at the default pool size")]
    [TestCase(8192, true, TestName = "keyed transactions at four times the default pool size")]
    [TestCase(20000, true, TestName = "keyed transactions at ten times the default pool size")]
    public async Task Measure(int senders, bool keyed)
    {
        (IWorldState world, IStateReader stateReader) = TestWorldStateFactory.CreateForTestWithStateReader();
        Address[] addresses = new Address[senders];
        Hash256 stateRoot;

        using (world.BeginScope(null))
        {
            for (int i = 0; i < senders; i++)
            {
                addresses[i] = AddressOf(i);
                world.CreateAccount(addresses[i], 1.Ether);
            }

            if (keyed)
            {
                // Non-empty, or the commit prunes NONCE_MANAGER and takes the slots below with it.
                world.CreateAccount(Eip8250Constants.NonceManagerAddress, 1, 1);
                foreach (Address sender in addresses)
                {
                    for (int k = 0; k < Eip8250Constants.MaxNonceKeys; k++)
                    {
                        world.Set(KeyedNonceManager.StorageSlot(sender, KeyOf(sender, k)), [(byte)KeyedSeq]);
                    }
                }
            }

            world.Commit(Cancun.Instance);
            world.CommitTree(0);
            stateRoot = world.StateRoot;
        }

        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithStateRoot(stateRoot).WithBaseFee(0).TestObject;
        TestBlockTree blockTree = new() { Head = Build.A.Block.WithHeader(header).TestObject, BestSuggestedHeader = header };
        ChainHeadInfoProvider headInfo = new(
            new ChainHeadSpecProvider(SpecProvider, blockTree),
            blockTree,
            new SpecificBlockReadOnlyStateProvider(stateReader, header));

        await using TxPool txPool = new(
            new EthereumEcdsa(SpecProvider.ChainId),
            new BlobTxStorage(),
            headInfo,
            new TxPoolConfig { Size = senders, BlobsSupport = BlobsSupportMode.Disabled },
            new TxValidator(SpecProvider.ChainId),
            new SpecChangeTxValidator(SpecProvider.ChainId),
            LimboLogs.Instance,
            new TransactionComparerProvider(SpecProvider, blockTree).GetDefaultComparer());

        // Admission is not what is being measured, and a keyed frame transaction cannot clear it without an EVM.
        TxDistinctSortedPool pool = txPool._transactions;
        for (int i = 0; i < senders; i++)
        {
            Transaction tx = BuildTx(i, addresses[i], keyed);
            Assert.That(pool.TryInsert(tx.Hash!, tx), Is.True, $"transaction {i} was not inserted");
        }

        // The readiness filter must reach every bucket's expensive branch, or the numbers below mean nothing.
        Assert.That(txPool.GetPendingTransactionsBySender(true, UInt256.Zero), Has.Count.EqualTo(senders));

        Report("filtered", () => txPool.GetPendingTransactionsBySender(true, UInt256.Zero), senders, keyed);
        Report("nonframe", () => txPool.GetPendingTransactionsBySenderWithReadyNonFrameTx(UInt256.Zero), senders, keyed);
        Report("unfiltered", () => txPool.GetPendingTransactionsBySender(), senders, keyed);
    }

    private static void Report(string label, Func<IDictionary<AddressAsKey, Transaction[]>> snapshot, int senders, bool keyed)
    {
        for (int i = 0; i < Warmup; i++) snapshot();

        double[] ms = new double[Samples];
        for (int i = 0; i < Samples; i++)
        {
            long start = Stopwatch.GetTimestamp();
            GC.KeepAlive(snapshot());
            ms[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(ms);
        TestContext.Out.WriteLine(
            $"RESULT {label} senders={senders} keyed={keyed} min={ms[0]:F3}ms p50={ms[Samples / 2]:F3}ms p90={ms[Samples * 9 / 10]:F3}ms max={ms[^1]:F3}ms");
    }

    /// <remarks>Big-endian, so distinct senders differ in the last byte: the pool's account cache shards on it.</remarks>
    private static Address AddressOf(int index)
    {
        Span<byte> bytes = stackalloc byte[Address.Size];
        bytes.Clear();
        BinaryPrimitives.WriteInt32BigEndian(bytes[(Address.Size - sizeof(int))..], index);
        return new Address(bytes);
    }

    private static UInt256 KeyOf(Address sender, int k)
    {
        UInt256 baseKey = new(Keccak.Compute(sender.Bytes).Bytes, isBigEndian: true);
        return baseKey + (UInt256)(k + 1);
    }

    private static UInt256[] KeysOf(Address sender)
    {
        UInt256[] keys = new UInt256[Eip8250Constants.MaxNonceKeys];
        for (int k = 0; k < keys.Length; k++) keys[k] = KeyOf(sender, k);
        return keys;
    }

    private static Transaction BuildTx(int index, Address sender, bool keyed)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithSenderAddress(sender)
            .WithGasLimit(21_000)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithHash(Keccak.Compute(((UInt256)index).ToBigEndian()));

        return keyed
            ? builder.WithType(TxType.FrameTx).WithNonce(KeyedSeq).WithNonceKeys(KeysOf(sender)).TestObject
            : builder.WithType(TxType.EIP1559).WithNonce(0).TestObject;
    }
}
