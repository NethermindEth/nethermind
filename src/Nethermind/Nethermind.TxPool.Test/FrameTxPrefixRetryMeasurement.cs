// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Spec;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

namespace Nethermind.TxPool.Test;

/// <summary>Measures how long an unincludable frame transaction stays pending, and therefore how many times a
/// block producer re-executes its validation prefix without ever collecting a fee for it.</summary>
/// <remarks>Results go to <c>FRAME_RETRY_OUT</c> (default <c>frame-prefix-retry.txt</c> under the temp
/// directory), because the test runner swallows console writers.</remarks>
[Explicit("measurement harness")]
public class FrameTxPrefixRetryMeasurement
{
    private const int HeadAdvances = 20;
    private const ulong SampleFrameGas = 50_000;
    private const ulong FirstHeadNumber = 10_000_000;
    private const ulong SlotSeconds = 12;
    private const ulong GenesisTimestamp = 1_700_000_000;

    private ILogManager _logManager = null!;
    private ISpecProvider _specProvider = null!;
    private EthereumEcdsa _ethereumEcdsa = null!;
    private TestReadOnlyStateProvider _stateProvider = null!;
    private TestBlockTree _blockTree = null!;
    private TxPool _txPool = null!;
    private ulong _headNumber;
    private ulong _headTimestamp;

    [SetUp]
    public void Setup()
    {
        _logManager = LimboLogs.Instance;
        _specProvider = new TestSpecProvider(Eip8141Prototype.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
        _stateProvider = new TestReadOnlyStateProvider();
        _blockTree = new TestBlockTree();
        _headNumber = FirstHeadNumber - 1;
        _headTimestamp = GenesisTimestamp;
        _blockTree.Head = BuildHead();
        _blockTree.BestSuggestedHeader = _blockTree.Head!.Header;
        _txPool = CreatePool();
        _stateProvider.CreateAccount(TestItem.AddressA, UInt256.MaxValue);
    }

    /// <summary>Positive control: without it the retention case below cannot tell "the pool keeps it" from
    /// "the harness never advanced the head".</summary>
    [Test]
    public async Task Control_included_frame_transaction_leaves_the_pool()
    {
        Transaction tx = BuildFrameTx(nonce: 0, deadline: null);
        Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(_txPool.GetPendingTransactionsCount(), Is.EqualTo(1), "the sample never entered the pool");

        await AdvanceHead(tx);

        Emit($"case=control_included pending_after_inclusion={_txPool.GetPendingTransactionsCount()}");
        Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero, "an included frame transaction was not evicted");
    }

    [Test]
    public async Task Unincludable_frame_transaction_survives_every_head()
    {
        Transaction tx = BuildFrameTx(nonce: 0, deadline: null);
        Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

        int survived = 0;
        for (int i = 0; i < HeadAdvances; i++)
        {
            await AdvanceHead(includedTx: null);
            // Membership in the pending set is what a producer actually reads, so assert that rather
            // than mere presence in the pool's hash index.
            if (Array.IndexOf(_txPool.GetPendingTransactions(), tx) >= 0) survived++;
        }

        ulong prefixGas = FrameTxValidation.ValidationWorkGas(tx);
        Emit($"case=no_deadline heads={HeadAdvances} survived_heads={survived} prefix_gas={prefixGas} "
             + $"unpaid_gas_over_window={prefixGas * (ulong)survived}");
        Assert.That(survived, Is.EqualTo(HeadAdvances), "the pool dropped an unincludable frame transaction on its own");
    }

    /// <summary>An expiry deadline bounds the same exposure in blocks rather than in gas.</summary>
    [Test]
    public async Task Expiry_deadline_bounds_the_number_of_attempts()
    {
        ulong deadline = _headTimestamp + SlotSeconds * 3;
        Transaction tx = BuildFrameTx(nonce: 0, deadline);
        Assert.That(_txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

        int survived = 0;
        for (int i = 0; i < HeadAdvances; i++)
        {
            await AdvanceHead(includedTx: null);
            if (!_txPool.TryGetPendingTransaction(tx.Hash!, out _)) break;
            survived++;
        }

        Emit($"case=with_deadline deadline_slots=3 heads={HeadAdvances} survived_heads={survived}");
        Assert.That(survived, Is.LessThan(HeadAdvances), "an expired frame transaction was never evicted");
    }

    private async Task AdvanceHead(Transaction? includedTx)
    {
        _headNumber++;
        _headTimestamp += SlotSeconds;
        Block block = includedTx is null ? BuildHead() : BuildHead(includedTx);

        Task waitTask = Wait.ForEventCondition<Block>(
            CancellationToken.None,
            e => _txPool.TxPoolHeadChanged += e,
            e => _txPool.TxPoolHeadChanged -= e,
            e => e.Number == block.Number);

        _blockTree.Head = block;
        _blockTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(block));
        await waitTask;
    }

    private Block BuildHead(params Transaction[] transactions) =>
        Build.A.Block
            .WithNumber(_headNumber)
            .WithTimestamp(_headTimestamp)
            .WithBaseFeePerGas(0)
            .WithGasLimit(30_000_000)
            .WithTransactions(transactions)
            .TestObject;

    /// <remarks>The prefix approves execution and payment from the sender — the layout EIP-8141 recognizes for
    /// the public mempool — so the sample is priced by the same path a real one would be.</remarks>
    private Transaction BuildFrameTx(ulong nonce, ulong? deadline)
    {
        TxFrame[] frames = deadline is null
            ? [SelfVerify(SampleFrameGas)]
            : [ExpiryAt(deadline.Value, SampleFrameGas), SelfVerify(SampleFrameGas)];

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = _specProvider.ChainId,
            Nonce = nonce,
            SenderAddress = TestItem.AddressA,
            Frames = frames,
            FrameSignatures = [],
            GasLimit = 1_000_000,
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private TxPool CreatePool()
    {
        ChainHeadInfoProvider headInfo = new(
            new ChainHeadSpecProvider(_specProvider, _blockTree),
            _blockTree,
            _stateProvider);

        return new TxPool(
            _ethereumEcdsa,
            new BlobTxStorage(),
            headInfo,
            new TxPoolConfig { GasLimit = 30_000_000 },
            new TxValidator(_specProvider.ChainId),
            new SpecChangeTxValidator(_specProvider.ChainId),
            _logManager,
            new TransactionComparerProvider(_specProvider, _blockTree).GetDefaultComparer(),
            ShouldGossip.Instance,
            incomingTxFilters: null,
            thereIsPriorityContract: false);
    }

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("FRAME_RETRY_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-prefix-retry.txt");
        File.AppendAllText(path, $"RESULT {line}{Environment.NewLine}");
    }
}
