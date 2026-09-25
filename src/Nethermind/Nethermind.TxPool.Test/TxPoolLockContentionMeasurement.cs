// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Spec;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>
/// Measures the two lock-held costs of the EIP-8141 pool: the ready-filtered bucket selection under the
/// pool-wide lock, and the per-head validation-prefix simulation under the head write lock.
/// </summary>
/// <remarks>
/// <c>Ready_filtered_selection</c> times two selections: the ready-filtered one is the whole cost, and the
/// unfiltered one is the bucket copy alone, which is all the pool lock is held for once readiness is judged on
/// the taken snapshot. A build that judges readiness during the pool walk instead holds the lock for the whole
/// of the first figure, so the pair reads as lock-held against total on either shape. Keyed readiness hashes
/// for real but reads its slots from an in-memory dictionary, so a node reading storage through the trie pays
/// more per selected key than this reports: the numbers are a floor.
///
/// <c>Revalidation_backlog_per_head</c> drives a node-bound backlog across a run of heads and reports what
/// each head re-simulates; sweeping the deferral budget to <see cref="int.MaxValue"/> reproduces an
/// unbounded carry.
///
/// Results are appended as <c>RESULT key=value</c> lines to <c>TXPOOL_LOCK_OUT</c>, or
/// <c>txpool-lock-contention.txt</c> in the temp directory.
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
[NonParallelizable]
public class TxPoolLockContentionMeasurement
{
    // The minimum over the samples is the headline: this is CPU-bound work, so the slower samples carry
    // scheduling and collection noise the comparison is not about.
    private const int Warmup = 50;
    private const int Samples = 200;
    private const int PoolSize = 16384;
    private const long TxGasLimit = 1_000_000;

    /// <summary>Heads driven by the revalidation sweep; enough to outlast any sane deferral budget.</summary>
    private const int Heads = 8;

    /// <summary>Stand-in cost of one validation-prefix simulation, at the low end of what an EVM run costs.</summary>
    private const int SimulateMicros = 500;

    /// <summary>Keyed sequence every seeded NONCE_MANAGER slot is parked at.</summary>
    private const ulong KeyedSeq = 7;

    private ISpecProvider _specProvider = null!;
    private IReleaseSpec _spec = null!;
    private TestReadOnlyStateProvider _state = null!;
    private TestBlockTree _blockTree = null!;
    private TxPool _txPool = null!;

    [SetUp]
    public void Setup()
    {
        _spec = new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip8250Enabled = true };
        _specProvider = new TestSpecProvider(_spec);
        _state = new TestReadOnlyStateProvider();
        _blockTree = new TestBlockTree
        {
            Head = Build.A.Block.WithNumber(9_999_999).WithBaseFeePerGas(0).TestObject,
            BestSuggestedHeader = Build.A.BlockHeader.WithNumber(10_000_000).WithBaseFee(0).TestObject
        };
    }

    [TearDown]
    public void TearDown() => _txPool?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    // 2,048 senders x 16 keys is the configured-pool-size worst case the readiness scan is bounded by.
    [TestCase(2048, Eip8250Constants.MaxNonceKeys, true)]
    [TestCase(2048, Eip8250Constants.MaxNonceKeys, false)]
    [TestCase(2048, 1, true)]
    public void Ready_filtered_selection(int senders, int keysPerTx, bool ready)
    {
        BuildPool(senders, keysPerTx, ready);

        List<double> filteredMillis = [];
        List<double> unfilteredMillis = [];

        for (int i = 0; i < Warmup + Samples; i++)
        {
            long started = Stopwatch.GetTimestamp();
            int selected = _txPool.GetPendingTransactionsBySender(filterToReadyTx: true).Count;
            double millis = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (i == 0)
            {
                Assert.That(selected, ready ? Is.EqualTo(senders) : Is.Zero,
                    "the selection did not see the readiness the seeded state was built for");
            }

            if (i >= Warmup) filteredMillis.Add(millis);
        }

        for (int i = 0; i < Warmup + Samples; i++)
        {
            long started = Stopwatch.GetTimestamp();
            _txPool.GetPendingTransactionsBySender();
            double millis = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (i >= Warmup) unfilteredMillis.Add(millis);
        }

        filteredMillis.Sort();
        unfilteredMillis.Sort();
        Emit($"case=ready_filtered_selection senders={senders} keys_per_tx={keysPerTx} ready={ready} "
             + $"samples={filteredMillis.Count} "
             + $"filtered_min_ms={filteredMillis[0]:F3} "
             + $"filtered_p50_ms={Percentile(filteredMillis, 0.50):F3} "
             + $"unfiltered_min_ms={unfilteredMillis[0]:F3} "
             + $"unfiltered_p50_ms={Percentile(unfilteredMillis, 0.50):F3}");
    }

    // int.MaxValue is the carry before it was bounded: a backlog the per-head budget cannot clear is
    // re-simulated in full on every later head, all of it under the head write lock.
    [TestCase(200, 2)]
    [TestCase(200, int.MaxValue)]
    public async Task Revalidation_backlog_per_head(int backlog, int deferralBudget)
    {
        CountingSimulator simulator = new();
        _txPool = CreatePool(deferralBudget, simulator);

        for (int i = 0; i < backlog; i++)
        {
            Address sender = SenderAt(i);
            _state.CreateAccount(sender, UInt256.MaxValue);
            Assert.That(_txPool.SubmitTx(BuildFrameTx(sender, [UInt256.Zero]), TxHandlingOptions.None),
                Is.EqualTo(AcceptTxResult.Accepted), $"{sender} did not enter the pool");
        }

        simulator.NodeBound = true;
        StringBuilder perHead = new();
        StringBuilder headMillis = new();
        Block head = _blockTree.Head!;

        for (int number = 1; number <= Heads; number++)
        {
            head = Build.A.Block.WithNumber(_blockTree.Head!.Number + (ulong)number).WithParent(head).TestObject;
            // Names an account nothing in the pool depends on, so only a carried deferral reaches the simulator.
            head.AccountChanges = new ArrayPoolList<AddressAsKey>(1) { TestItem.AddressF };

            simulator.Calls = 0;
            long started = Stopwatch.GetTimestamp();
            await RaiseBlockAddedToMainAndWaitForNewHead(head);
            double millis = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            perHead.Append(simulator.Calls).Append(number == Heads ? "" : ",");
            headMillis.Append($"{millis:F1}").Append(number == Heads ? "" : ",");
        }

        Emit($"case=revalidation_backlog backlog={backlog} deferral_budget={deferralBudget} "
             + $"simulate_us={SimulateMicros} heads={Heads} "
             + $"simulations_total={simulator.Total} simulations_per_head={perHead} head_ms={headMillis}");
    }

    /// <summary>Stands in for a validation prefix at a fixed cost, so the variable is how often it runs.</summary>
    private sealed class CountingSimulator : IFrameTxPrefixSimulator
    {
        public bool NodeBound { get; set; }
        public int Calls { get; set; }
        public int Total { get; private set; }

        public FrameTxSimulationResult Simulate(
            Transaction tx, bool signaturesPreValidated = false, bool local = false, CancellationToken token = default)
        {
            Calls++;
            Total++;
            if (!NodeBound) return FrameTxSimulationResult.Accept(tx.SenderAddress!);

            long until = Stopwatch.GetTimestamp() + (long)(SimulateMicros / 1_000_000d * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
            return FrameTxSimulationResult.RejectIndeterminate("budget exhausted");
        }
    }

    private async Task RaiseBlockAddedToMainAndWaitForNewHead(Block block)
    {
        Task waitTask = Wait.ForEventCondition<Block>(
            CancellationToken.None,
            e => _txPool.TxPoolHeadChanged += e,
            e => _txPool.TxPoolHeadChanged -= e,
            e => e.Number == block.Number);

        _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(block));
        await waitTask;
    }

    private void BuildPool(int senders, int keysPerTx, bool ready)
    {
        // The singleton [0] aliases the account nonce, so it is the ordinary, unkeyed shape.
        UInt256[] nonceKeys = keysPerTx == 1 ? [UInt256.Zero] : BuildNonceKeys(keysPerTx);
        List<Address> seeded = [with(senders)];

        for (int i = 0; i < senders; i++)
        {
            Address sender = SenderAt(i);
            seeded.Add(sender);
            _state.CreateAccount(sender, UInt256.MaxValue);
            SeedNonceKeys(sender, nonceKeys, KeyedSeq);
        }

        _txPool = CreatePool();

        foreach (Address sender in seeded)
        {
            AcceptTxResult accepted = _txPool.SubmitTx(BuildFrameTx(sender, nonceKeys), TxHandlingOptions.None);
            Assert.That(accepted, Is.EqualTo(AcceptTxResult.Accepted), $"{sender} did not enter the pool");
        }

        if (ready) return;

        // Advancing every sequence past the pooled one makes each bucket scan to its end without a verdict,
        // which is what the scan costs in full rather than on its first entry.
        foreach (Address sender in seeded)
        {
            if (keysPerTx == 1) _state.CreateAccount(sender, UInt256.MaxValue, nonce: 1);
            else SeedNonceKeys(sender, nonceKeys, KeyedSeq + 1);
        }
    }

    private void SeedNonceKeys(Address sender, UInt256[] nonceKeys, ulong seq)
    {
        foreach (UInt256 nonceKey in nonceKeys)
        {
            if (nonceKey.IsZero) continue;
            _state.Set(KeyedNonceManager.StorageSlot(sender, nonceKey), seq);
        }
    }

    private static UInt256[] BuildNonceKeys(int count)
    {
        UInt256[] keys = new UInt256[count];
        for (int i = 0; i < count; i++) keys[i] = (UInt256)(i + 1);
        return keys;
    }

    private static Address SenderAt(int index)
    {
        byte[] bytes = new byte[Address.Size];
        BitConverter.TryWriteBytes(bytes.AsSpan(Address.Size - sizeof(int)), index + 1);
        return new Address(bytes);
    }

    /// <summary>A self-verifying frame transaction: no signature to recover, so a sender needs no key pair.</summary>
    private Transaction BuildFrameTx(Address sender, UInt256[] nonceKeys)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = _specProvider.ChainId,
            Nonce = KeyedNonceManager.UsesKeyedDomain(nonceKeys) ? KeyedSeq : 0,
            SenderAddress = sender,
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 50_000, UInt256.Zero, default)],
            NonceKeys = nonceKeys,
            FrameSignatures = [],
            GasLimit = TxGasLimit,
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private TxPool CreatePool(int deferralBudget, IFrameTxPrefixSimulator simulator) =>
        CreatePool(new TxPoolConfig
        {
            Size = PoolSize,
            GasLimit = TxGasLimit,
            FrameTxMaxVerifyGas = 0,
            FrameTxRevalidationDeferralBudget = deferralBudget
        }, simulator);

    private TxPool CreatePool() =>
        CreatePool(new TxPoolConfig { Size = PoolSize, GasLimit = TxGasLimit, FrameTxMaxVerifyGas = 0 }, null);

    private TxPool CreatePool(TxPoolConfig config, IFrameTxPrefixSimulator? simulator)
    {
        ChainHeadInfoProvider headInfo = new(new ChainHeadSpecProvider(_specProvider, _blockTree), _blockTree, _state);

        return new TxPool(
            new EthereumEcdsa(_specProvider.ChainId),
            new BlobTxStorage(),
            headInfo,
            config,
            new TxValidator(_specProvider.ChainId),
            new SpecChangeTxValidator(_specProvider.ChainId),
            LimboLogs.Instance,
            new TransactionComparerProvider(_specProvider, _blockTree).GetDefaultComparer(),
            ShouldGossip.Instance,
            incomingTxFilters: null,
            thereIsPriorityContract: false,
            simulator);
    }

    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        int index = (int)(percentile * (sorted.Count - 1));
        return sorted[index];
    }

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("TXPOOL_LOCK_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "txpool-lock-contention.txt");
        string record = $"RESULT {line}";
        TestContext.Out.WriteLine(record);
        File.AppendAllText(path, record + Environment.NewLine);
    }
}
