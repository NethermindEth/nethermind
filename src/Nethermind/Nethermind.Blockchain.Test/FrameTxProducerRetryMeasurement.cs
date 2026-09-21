// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>Measures how often a producer re-executes a frame transaction whose validation prefix can never
/// approve, and how much unpaid gas each attempt burns.</summary>
/// <remarks>A harness for sizing <c>MAX_VERIFY_GAS</c>, not an assertion of behaviour: it answers whether the
/// unpaid work is bounded by one block or multiplied by the blocks the transaction survives in the pool.</remarks>
[TestFixture]
[Explicit("measurement harness")]
public class FrameTxProducerRetryMeasurement
{
    private const long BlockGasLimit = 30_000_000;
    private const int Attempts = 20;

    /// <summary>The share of its granted budget a never-approving attempt must consume to count as a burn.</summary>
    private const double BudgetBurnFloor = 0.99;

    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Beneficiary = TestItem.AddressE;

    private ISpecProvider _specProvider = null!;
    private IWorldState _stateProvider = null!;
    private ITransactionProcessor _transactionProcessor = null!;
    private BasicTestBlockchain? _chain;
    private IReadOnlyTxProcessorSource? _source;
    private IReadOnlyTxProcessingScope? _scope;

    private IReleaseSpec Spec => _specProvider.GenesisSpec;

    /// <summary>Takes the processing stack from the chain's production wiring, with the measured sender's code
    /// placed in genesis; only the executor under measurement and the gates it drives are built here.</summary>
    private async Task BuildChain(byte[] senderCode)
    {
        _specProvider = new TestSpecProvider(Eip8141Prototype.Instance);
        _chain = await BasicTestBlockchain.Create(builder =>
        {
            builder.AddSingleton(_specProvider);
            builder.WithGenesisPostProcessor((_, worldState, specProvider) =>
            {
                // Replaces the account TestBlockchain funds in genesis, clearing the placeholder code and
                // storage slot it puts on this address so only the measured code is reachable.
                worldState.CreateAccount(Sender, 100.Ether);
                worldState.InsertCode(Sender, senderCode, specProvider.GenesisSpec);
                worldState.RecalculateStateRoot();
            });
        });

        _source = _chain.ReadOnlyTxProcessingEnvFactory.Create();
        _scope = _source.Build(_chain.BlockTree.Head?.Header);
        _stateProvider = _scope.WorldState;
        _transactionProcessor = _scope.TransactionProcessor;
    }

    [TearDown]
    public void TearDown()
    {
        _scope?.Dispose();
        _source?.Dispose();
        _chain?.Dispose();
    }

    /// <summary>A prefix that never approves: it loops until the frame's gas limit is exhausted.</summary>
    private static byte[] NeverApproves() =>
        Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done;

    private static byte[] Approves() =>
        Prepare.EvmCode
            .PushData((byte)FrameFlags.ApproveExecutionAndPayment)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.APPROVE)
            .Done;

    /// <summary>Measures producer-side retry behavior for a control that approves and a prefix that never does.</summary>
    /// <remarks>322,800 is soispoke's declared privacy-pool budget (their
    /// <c>activation_manifest.testbed.json</c>: <c>verify_frame_gas</c> 320,000 + <c>signature_gas</c> 2,800).
    /// The <c>groth16-soispoke</c> sweep entry in the mempool/flood harnesses stays clamped to 300,000
    /// because those harnesses run through <c>CapFrameGas</c>, which enforces the fixed
    /// <see cref="Eip8141Constants.MaxVerifyGas"/> on the simulation path regardless of configuration; this
    /// fixture drives block execution instead, which applies no such cap, so it can use the real declared
    /// number that clamp stands in for. <see cref="FrameTx"/> puts the whole declared value into one frame's
    /// execution gas limit with no signatures, so at every ceiling this fixture sweeps (not only 322,800) the
    /// EVM burn is a uniform tight-loop shape, distinct from the signature/Groth16 shapes the mempool/flood
    /// harnesses measure at the same nominal ceiling — rows here are not CPU-comparable to those, only the
    /// declared-gas axis is shared.</remarks>
    [TestCase(true, 322_800ul, TestName = "control: a prefix that approves is included and paid for")]
    [TestCase(false, 300_000ul, TestName = "never approves, at the default MAX_VERIFY_GAS")]
    [TestCase(false, 322_800ul, TestName = "never approves, at soispoke's declared privacy-pool budget")]
    public async Task ProducerRetriesAFailingPrefix(bool approves, ulong verifyGas)
    {
        await BuildChain(approves ? Approves() : NeverApproves());

        CountingAdapter adapter = new(new BuildUpTransactionProcessorAdapter(_transactionProcessor));
        BlockProcessor.BlockProductionTransactionPicker picker = new(_specProvider);
        IBlockAccessListManager balManager = Substitute.For<IBlockAccessListManager>();
        balManager.Enabled.Returns(false);
        BlockProcessor.BlockProductionTransactionsExecutor executor =
            new(adapter, _stateProvider, picker, LimboLogs.Instance, balManager, NullTxPool.Instance);

        Transaction tx = FrameTx(verifyGas);
        UInt256 beneficiaryBefore = _stateProvider.GetBalance(Beneficiary);
        UInt256 senderBefore = _stateProvider.GetBalance(Sender);

        int included = 0;
        for (int i = 0; i < Attempts; i++)
        {
            Block block = Build.A.Block
                .WithNumber(1 + i)
                .WithBaseFeePerGas(UInt256.Zero)
                .WithBeneficiary(Beneficiary)
                .WithGasLimit(BlockGasLimit)
                .WithTransactions(tx)
                .TestObject;

            BlockReceiptsTracer receiptsTracer = new();
            receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
            receiptsTracer.StartNewBlockTrace(block);
            executor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Spec));
            executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, receiptsTracer, CancellationToken.None);
            receiptsTracer.EndBlockTrace();

            // The executor rewrites TxRoot from the transactions it actually included, so an empty
            // trie root is the producer saying it built a block without this transaction.
            if (block.Header.TxRoot != Keccak.EmptyTreeHash) included++;
        }

        UInt256 beneficiaryDelta = _stateProvider.GetBalance(Beneficiary) - beneficiaryBefore;
        UInt256 senderDelta = senderBefore - _stateProvider.GetBalance(Sender);

        ulong burned = 0;
        foreach (ulong b in adapter.BurnedPerAttempt) burned += b;
        ulong firstBurn = adapter.BurnedPerAttempt.Count > 0 ? adapter.BurnedPerAttempt[0] : 0;

        Emit($"case={(approves ? "control_approves" : "never_approves")} blocks={Attempts} "
             + $"execution_attempts={adapter.Attempts} included_blocks={included} "
             + $"burn_first_attempt={firstBurn} burn_total={burned} budget={verifyGas} "
             + $"beneficiary_delta={beneficiaryDelta} sender_delta={senderDelta} "
             );

        if (approves)
        {
            using (Assert.EnterMultipleScope())
            {
                // Once included the sender's nonce has advanced, so the picker declines it on every later block.
                Assert.That(included, Is.EqualTo(1), "the control must be includable, or the harness proves nothing about the failing case");
                Assert.That(beneficiaryDelta, Is.GreaterThan(UInt256.Zero), "an included transaction must pay the fee recipient");
                Assert.That(senderDelta, Is.EqualTo(beneficiaryDelta), "the payer must fund exactly what the fee recipient received");
            }
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(included, Is.Zero, "a prefix that never approves must not be built into a block");
                Assert.That(adapter.Attempts, Is.EqualTo(Attempts), "each block attempt must re-execute the transaction");
                Assert.That(firstBurn, Is.GreaterThan((ulong)(verifyGas * BudgetBurnFloor)), "the whole verification budget must be burned per attempt");
                Assert.That(beneficiaryDelta, Is.EqualTo(UInt256.Zero), "the work was paid for after all");
                Assert.That(senderDelta, Is.EqualTo(UInt256.Zero), "the sender was charged after all");
            }
        }
    }

    private static IEnumerable<TestCaseData> RetryCases()
    {
        foreach (ulong verifyGas in new ulong[] { 100_000ul, 236_285ul, 300_000ul, 322_800ul, 500_000ul })
        {
            foreach (int kRetry in new int[] { 1, 2, 4, 8 })
            {
                yield return new TestCaseData(verifyGas, kRetry);
            }
        }
    }

    /// <summary>
    /// Measures the unpaid verification work extracted from a producer by a never-approving prefix
    /// that is evicted after failing on <paramref name="kRetry"/> distinct chain heads, one attempt per head.
    /// </summary>
    [TestCaseSource(nameof(RetryCases))]
    public async Task ProducerRetriesAreBoundedByKRetry(ulong verifyGas, int kRetry)
    {
        (int blocksOffered, int attempts, int headsFailed, int evictionCalls, ulong firstBurn, ulong burned,
                UInt256 beneficiaryDelta, _) =
            await RunNeverApprovingSweep(verifyGas, kRetry, mPerHead: 1);

        Emit($"case=k_retry_sweep k_retry={kRetry} k_basis=modelled m_per_head=1 m_effective=1 budget={verifyGas} "
             + $"blocks_offered={blocksOffered} execution_attempts={attempts} eviction_calls={evictionCalls} "
             + $"burn_first_attempt={firstBurn} burn_total={burned} "
             + $"amplification={(firstBurn == 0 ? 0 : (double)burned / firstBurn):F2} amplification_basis=closed_form "
             + $"beneficiary_delta={beneficiaryDelta}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headsFailed, Is.EqualTo(kRetry),
                "the pool must evict the transaction after exactly K_retry failed heads, or the loop bound alone is proving nothing");
            Assert.That(evictionCalls, Is.EqualTo(blocksOffered),
                "the executor must consult the pool's eviction decision exactly once per attempt, or a double-call per attempt would go unnoticed");
            Assert.That(attempts, Is.EqualTo(kRetry),
                "the producer must re-execute the prefix exactly K_retry times before the pool evicts it");
            Assert.That(firstBurn, Is.GreaterThan((ulong)(verifyGas * BudgetBurnFloor)),
                "each attempt must burn the whole verification budget, or the amplification is measured against the wrong unit");
            Assert.That(burned, Is.GreaterThan((ulong)(firstBurn * (ulong)kRetry * BudgetBurnFloor)),
                "total unpaid burn must scale with K_retry, which is the quantity this sweep exists to report");
            Assert.That(beneficiaryDelta, Is.EqualTo(UInt256.Zero),
                "the work was paid for after all, so it is not unpaid burn");
        }
    }

    /// <summary>
    /// Measures unpaid verification burn as a function of both K_retry (distinct chain heads survived)
    /// and M (production attempts spent against the same head before it advances).
    /// </summary>
    /// <remarks>
    /// The pool's eviction budget counts one unit per distinct chain head, not per production attempt:
    /// repeated attempts against an unchanged head are free until the head advances, so total unpaid
    /// burn scales with K_retry times M, not K_retry alone. M is a modelled sweep input here, not a
    /// measured one. At <c>kRetry == 1</c>, M has no effect (eviction happens on the first attempt
    /// regardless), so those rows are a documented control rather than a distinct measurement.
    /// </remarks>
    [Test]
    public async Task ProducerRetriesAreBoundedByKRetryAndAttemptsPerHead(
        [Values(1, 2, 4, 8)] int kRetry,
        [Values(1, 8, 32, 128)] int mPerHead)
    {
        const ulong verifyGas = 300_000ul;
        int mEffective = kRetry == 1 ? 1 : mPerHead;

        (int blocksOffered, int attempts, int headsFailed, int evictionCalls, ulong firstBurn, ulong burned,
                UInt256 beneficiaryDelta, int attemptCap) =
            await RunNeverApprovingSweep(verifyGas, kRetry, mPerHead);

        Emit($"case=k_retry_two_axis k_retry={kRetry} k_basis=modelled m_per_head={mPerHead} m_basis=modelled "
             + $"m_effective={mEffective} budget={verifyGas} "
             + $"blocks_offered={blocksOffered} execution_attempts={attempts} eviction_calls={evictionCalls} "
             + $"burn_first_attempt={firstBurn} burn_total={burned} "
             + $"amplification={(firstBurn == 0 ? 0 : (double)burned / firstBurn):F2} amplification_basis=closed_form "
             + $"beneficiary_delta={beneficiaryDelta}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headsFailed, Is.EqualTo(kRetry),
                "the transaction must fail on exactly K_retry distinct heads before eviction");
            Assert.That(evictionCalls, Is.EqualTo(blocksOffered),
                "the executor must consult the pool's eviction decision exactly once per attempt, or a double-call per attempt would go unnoticed");
            Assert.That(blocksOffered, Is.EqualTo(attemptCap),
                "total attempts must equal (K_retry - 1) full heads of M attempts plus one on the evicting head");
            Assert.That(attempts, Is.EqualTo(blocksOffered),
                "the producer must re-execute the prefix once per attempt, regardless of which head it targets");
            Assert.That(firstBurn, Is.GreaterThan((ulong)(verifyGas * BudgetBurnFloor)),
                "each attempt must burn the whole verification budget, or the amplification is measured against the wrong unit");
            Assert.That(burned, Is.GreaterThan((ulong)(firstBurn * (ulong)blocksOffered * BudgetBurnFloor)),
                "total unpaid burn must scale with attempts, which is K_retry times M, not K_retry alone");
            Assert.That(beneficiaryDelta, Is.EqualTo(UInt256.Zero),
                "the work was paid for after all, so it is not unpaid burn");
        }
    }

    /// <summary>
    /// Runs a never-approving frame tx through repeated production attempts against a modelled
    /// per-head eviction budget, until the pool evicts it. Shared by both sweeps above; the single-axis
    /// sweep is the case <paramref name="mPerHead"/> == 1.
    /// </summary>
    private async Task<(int BlocksOffered, int Attempts, int HeadsFailed, int EvictionCalls, ulong FirstBurn, ulong Burned, UInt256 BeneficiaryDelta, int AttemptCap)>
        RunNeverApprovingSweep(ulong verifyGas, int kRetry, int mPerHead)
    {
        // (kRetry - 1) full heads of mPerHead free attempts each, plus one attempt on the head that
        // brings the failed-heads tally to kRetry and evicts.
        int attemptCap = (kRetry - 1) * mPerHead + 1;

        await BuildChain(NeverApproves());

        CountingAdapter adapter = new(new BuildUpTransactionProcessorAdapter(_transactionProcessor));
        BlockProcessor.BlockProductionTransactionPicker picker = new(_specProvider);
        IBlockAccessListManager balManager = Substitute.For<IBlockAccessListManager>();
        balManager.Enabled.Returns(false);

        // Reproduces the pool's real per-head bookkeeping: the failed-heads tally advances only the
        // first time a given head generation is seen, so repeated attempts against the same head spend
        // no further budget.
        long headGeneration = 0;
        long lastCountedGeneration = -1;
        int headsFailed = 0;
        // Tracks every call independent of the per-head tally above, so a hypothetical double-call per
        // attempt (e.g. a regression in the executor's eviction path) still shows up as evictionCalls
        // exceeding blocksOffered, the way the single flat counter this replaced would have caught it.
        int evictionCalls = 0;
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.EvictTransaction(Arg.Any<Transaction>()).Returns(_ =>
        {
            evictionCalls++;
            if (lastCountedGeneration != headGeneration)
            {
                lastCountedGeneration = headGeneration;
                headsFailed++;
            }
            return headsFailed >= kRetry;
        });

        BlockProcessor.BlockProductionTransactionsExecutor executor =
            new(adapter, _stateProvider, picker, LimboLogs.Instance, balManager, txPool);

        Transaction tx = FrameTx(verifyGas);
        UInt256 beneficiaryBefore = _stateProvider.GetBalance(Beneficiary);

        int attemptsAtCurrentHead = 0;
        int blocksOffered = 0;
        while (blocksOffered < attemptCap && headsFailed < kRetry)
        {
            // Block number advances every attempt; headGeneration below is the modelled quantity that
            // stays fixed across the mPerHead attempts spent against one head.
            Block block = Build.A.Block
                .WithNumber(1 + blocksOffered)
                .WithBaseFeePerGas(UInt256.Zero)
                .WithBeneficiary(Beneficiary)
                .WithGasLimit(BlockGasLimit)
                .WithTransactions(tx)
                .TestObject;

            BlockReceiptsTracer receiptsTracer = new();
            receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
            receiptsTracer.StartNewBlockTrace(block);
            executor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Spec));
            executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, receiptsTracer, CancellationToken.None);
            receiptsTracer.EndBlockTrace();
            blocksOffered++;
            attemptsAtCurrentHead++;

            if (block.Header.TxRoot != Keccak.EmptyTreeHash)
            {
                Assert.Fail($"a prefix that never approves was built into block {blocksOffered}");
            }

            if (attemptsAtCurrentHead >= mPerHead)
            {
                headGeneration++;
                attemptsAtCurrentHead = 0;
            }
        }

        ulong burned = 0;
        foreach (ulong b in adapter.BurnedPerAttempt) burned += b;
        ulong firstBurn = adapter.BurnedPerAttempt.Count > 0 ? adapter.BurnedPerAttempt[0] : 0;

        return (blocksOffered, adapter.Attempts, headsFailed, evictionCalls, firstBurn, burned,
            _stateProvider.GetBalance(Beneficiary) - beneficiaryBefore, attemptCap);
    }

    private static Transaction FrameTx(ulong verifyGas)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Sender,
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: verifyGas, UInt256.Zero, default)],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };
        tx.GasLimit = verifyGas;
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("FRAME_RETRY_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-producer-retry.txt");
        File.AppendAllText(path, $"RESULT {line}{Environment.NewLine}");
    }
}
