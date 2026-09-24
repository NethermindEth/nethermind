// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Spec;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
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

    /// <summary>How long a sweep waits for the pool to acknowledge an advanced chain head.</summary>
    private static readonly TimeSpan HeadChangeTimeout = TimeSpan.FromSeconds(10);

    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Beneficiary = TestItem.AddressE;

    private ISpecProvider _specProvider = null!;
    private IWorldState _stateProvider = null!;
    private ITransactionProcessor _transactionProcessor = null!;
    private BasicTestBlockchain? _chain;
    private IReadOnlyTxProcessorSource? _source;
    private IReadOnlyTxProcessingScope? _scope;
    private TxPool.TxPool? _pool;
    private PoolHeadTree _poolHeadTree = null!;
    private ulong _poolHeadNumber;

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
    public async Task TearDown()
    {
        if (_pool is not null) await _pool.DisposeAsync();
        _pool = null;
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

    /// <summary>Builds the production executor under measurement over a counting adapter, so every sweep drives
    /// the same stack and differs only in the pool it consults.</summary>
    private BlockProcessor.BlockProductionTransactionsExecutor BuildExecutor(ITxPool txPool, out CountingAdapter adapter)
    {
        adapter = new CountingAdapter(new BuildUpTransactionProcessorAdapter(_transactionProcessor));
        IBlockAccessListManager balManager = Substitute.For<IBlockAccessListManager>();
        balManager.Enabled.Returns(false);
        return new BlockProcessor.BlockProductionTransactionsExecutor(
            adapter, _stateProvider, new BlockProcessor.BlockProductionTransactionPicker(_specProvider),
            LimboLogs.Instance, balManager, txPool);
    }

    /// <summary>Offers a block carrying only <paramref name="tx"/> to the producer's transaction executor.</summary>
    /// <returns>The block the executor built. The executor rewrites its <c>TxRoot</c> from the transactions it
    /// actually included, so an empty trie root is the producer saying it built a block without this one.</returns>
    /// <param name="elapsed">Wall time inside <c>ProcessTransactions</c> alone, excluding tracer setup.</param>
    private Block OfferBlock(
        BlockProcessor.BlockProductionTransactionsExecutor executor, Transaction tx, ulong number, out TimeSpan elapsed)
    {
        Block block = Build.A.Block
            .WithNumber(number)
            .WithBaseFeePerGas(UInt256.Zero)
            .WithBeneficiary(Beneficiary)
            .WithGasLimit(BlockGasLimit)
            .WithTransactions(tx)
            .TestObject;

        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
        receiptsTracer.StartNewBlockTrace(block);
        executor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Spec));

        long started = Stopwatch.GetTimestamp();
        executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, receiptsTracer, CancellationToken.None);
        elapsed = Stopwatch.GetElapsedTime(started);
        receiptsTracer.EndBlockTrace();

        return block;
    }

    /// <summary>Measures producer-side retry behavior for a control that approves and a prefix that never does.</summary>
    /// <remarks>352,800 is soispoke's declared privacy-pool budget (their
    /// <c>activation_manifest.testbed.json</c>: an EIP-8272 recent-root <c>verify_frame_gas</c> 30,000 +
    /// a pool <c>verify_frame_gas</c> 320,000 + <c>signature_gas</c> 2,800).
    /// The <c>groth16-soispoke</c> sweep entry in the mempool/flood harnesses stays clamped to 300,000
    /// because those harnesses run through <c>CapFrameGas</c>, which enforces the fixed
    /// <see cref="Eip8141Constants.MaxVerifyGas"/> on the simulation path regardless of configuration; this
    /// fixture drives block execution instead, which applies no such cap, so it can use the real declared
    /// number that clamp stands in for. <see cref="FrameTx"/> puts the whole declared value into one frame's
    /// execution gas limit with no signatures, so at every ceiling this fixture sweeps (not only 352,800) the
    /// EVM burn is a uniform tight-loop shape, distinct from the signature/Groth16 shapes the mempool/flood
    /// harnesses measure at the same nominal ceiling — rows here are not CPU-comparable to those, only the
    /// declared-gas axis is shared.</remarks>
    [TestCase(true, 352_800ul, TestName = "control: a prefix that approves is included and paid for")]
    [TestCase(false, 300_000ul, TestName = "never approves, at the default MAX_VERIFY_GAS")]
    [TestCase(false, 352_800ul, TestName = "never approves, at soispoke's declared privacy-pool budget")]
    public async Task ProducerRetriesAFailingPrefix(bool approves, ulong verifyGas)
    {
        await BuildChain(approves ? Approves() : NeverApproves());

        BlockProcessor.BlockProductionTransactionsExecutor executor =
            BuildExecutor(NullTxPool.Instance, out CountingAdapter adapter);

        Transaction tx = FrameTx(verifyGas);
        UInt256 beneficiaryBefore = _stateProvider.GetBalance(Beneficiary);
        UInt256 senderBefore = _stateProvider.GetBalance(Sender);

        int included = 0;
        for (int i = 0; i < Attempts; i++)
        {
            Block block = OfferBlock(executor, tx, (ulong)(1 + i), out _);
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
        foreach (ulong verifyGas in new ulong[] { 100_000ul, 236_285ul, 300_000ul, 352_800ul, 500_000ul })
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
    /// Kept alongside <see cref="MeasuredProducerRetriesAgainstARealPool"/>, which sweeps the same grid against
    /// the real pool, so the two emitted rows can be diffed; this one is the substituted-pool arm of that pair
    /// and reports an eviction-call count the real pool exposes no counter for.
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

        BlockProcessor.BlockProductionTransactionsExecutor executor = BuildExecutor(txPool, out CountingAdapter adapter);

        Transaction tx = FrameTx(verifyGas);
        UInt256 beneficiaryBefore = _stateProvider.GetBalance(Beneficiary);

        int attemptsAtCurrentHead = 0;
        int blocksOffered = 0;
        while (blocksOffered < attemptCap && headsFailed < kRetry)
        {
            // Block number advances every attempt; headGeneration below is the modelled quantity that
            // stays fixed across the mPerHead attempts spent against one head.
            Block block = OfferBlock(executor, tx, (ulong)(1 + blocksOffered), out _);
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

    /// <summary>
    /// Measures, against a real <see cref="TxPool.TxPool"/> whose chain head the sweep advances, how many times a
    /// producer re-executes a never-approving validation prefix before the pool drops it, and what each attempt burns.
    /// </summary>
    /// <remarks>
    /// The companion of <see cref="ProducerRetriesAreBoundedByKRetryAndAttemptsPerHead"/>, which models the same
    /// two axes over a substituted pool. Everything that decides the count is real here: the budget comes from
    /// <see cref="ITxPoolConfig.FrameTxEvictionRetryBudget"/>, the heads it is spent against are the pool's own
    /// head generations, and the drop is the pool removing the transaction from its pending set. Only the
    /// producer's offer loop is the harness: a real producer re-offers whatever the pool still holds, so the sweep
    /// stops offering as soon as the pool stops holding it, and the measured attempt count is the answer.
    /// The row carries the modelled figure beside the measured one so the closed form can be checked rather than
    /// assumed.
    /// No validation-prefix simulator is wired, which is the documented "admitted unresolved" path of
    /// <see cref="IFrameTxPrefixSimulator"/>: admission never runs the prefix, so a transaction that can only
    /// fail at production reaches the pool. With one wired this prefix is rejected at admission instead, and the
    /// budget is then reachable only by a prefix that approved at admission and fails later on changed head
    /// state, which is the case the budget exists for. Either way the bookkeeping measured here is the same, since
    /// the simulator sits on admission and not on <see cref="ITxPool.EvictTransaction"/>.
    /// The pool's head view and the producer's world state are separate on purpose: the pool prices the
    /// transaction at its own head while the producer runs the EVM at the chain the fixture built, which is where
    /// the never-approving code lives.
    /// Timings are taken with burn tracing on, which selects the instrumented EVM path, so they are named apart
    /// and are not comparable with the flood harness's <c>t_reject</c>. They are also tier-dependent: the runtime
    /// promotes the interpreter loop only after a stretch long enough that building the next case's chain stops
    /// restarting its call-counting delay, and the un-promoted attempt is the dearer of the two by a wide
    /// margin. Only rows whose attempt count is in the hundreds report the steady state, so read the low ones
    /// as an upper bound.
    /// The attempt count and the gas are unaffected, being counts rather than times.
    /// </remarks>
    [Test]
    [NonParallelizable]
    public async Task MeasuredProducerRetriesAgainstARealPool(
        [Values(1, 2, 4, 8)] int kRetry,
        [Values(1, 8, 32, 128)] int mPerHead)
    {
        const ulong verifyGas = 300_000ul;
        int modelledAttempts = (kRetry - 1) * mPerHead + 1;

        await BuildChain(NeverApproves());
        Transaction tx = FrameTx(verifyGas);
        CreatePool(kRetry);
        Assert.That(_pool!.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted),
            "the sample never entered the pool, so nothing below measures the pool's budget");

        (ProducerLoopResult run, CountingAdapter adapter) = await OfferUntilDropped(tx, kRetry, mPerHead);

        ulong burned = 0;
        foreach (ulong b in adapter.BurnedPerAttempt) burned += b;
        ulong firstBurn = adapter.BurnedPerAttempt.Count > 0 ? adapter.BurnedPerAttempt[0] : 0;

        Emit($"case=k_retry_measured k_retry={kRetry} k_basis=measured m_per_head={mPerHead} m_basis=scheduled "
             + $"m_effective={run.MaxAttemptsOnOneHead} budget={verifyGas} admission_basis=unresolved_admit "
             + $"blocks_offered={run.Attempts} execution_attempts={adapter.Attempts} "
             + $"heads_spent={run.HeadsAdvanced + 1} dropped_by_pool={run.Dropped} "
             + $"burn_first_attempt={firstBurn} burn_total={burned} "
             + $"amplification={(firstBurn == 0 ? 0 : (double)burned / firstBurn):F2} amplification_basis=measured "
             + $"modelled_attempts={modelledAttempts} modelled_amplification={modelledAttempts:F2} "
             + $"attempt_min_us_instrumented={Percentile(run.Micros, 0):F1} "
             + $"attempt_p50_us_instrumented={Percentile(run.Micros, 0.5):F1} "
             + $"attempt_total_ms_instrumented={run.TotalMicros / 1000:F1}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Dropped, Is.True,
                "the pool must drop the transaction within the offer cap, or the budget is not what bounds this");
            Assert.That(run.Attempts, Is.EqualTo(modelledAttempts),
                "the measured attempt count must equal the closed form (K_retry - 1) * M + 1, which is the claim this sweep exists to test");
            Assert.That(adapter.Attempts, Is.EqualTo(run.Attempts),
                "every offer must reach the EVM, or the burn is charged against the wrong denominator");
            Assert.That(firstBurn, Is.GreaterThan((ulong)(verifyGas * BudgetBurnFloor)),
                "each attempt must burn the whole verification budget");
            Assert.That(burned, Is.GreaterThan((ulong)(firstBurn * (ulong)run.Attempts * BudgetBurnFloor)),
                "total unpaid burn must scale with the measured attempts");
        }
    }

    /// <summary>
    /// Measures what a peer gets for re-gossiping a transaction the pool has already dropped: the budget is
    /// counted per pool residency, so each re-entry is a fresh one and the unpaid burn is unbounded in residencies.
    /// </summary>
    /// <remarks>Pins the semantics <see cref="ITxPoolConfig.FrameTxEvictionRetryBudget"/> documents, on the
    /// production path rather than by calling <see cref="ITxPool.EvictTransaction"/> directly.</remarks>
    [Test]
    [NonParallelizable]
    public async Task AResubmittedFrameTxIsGrantedAFreshBudget()
    {
        const ulong verifyGas = 300_000ul;
        const int kRetry = 2;
        const int mPerHead = 4;
        const int residencies = 3;
        int perResidency = (kRetry - 1) * mPerHead + 1;

        await BuildChain(NeverApproves());
        Transaction tx = FrameTx(verifyGas);
        CreatePool(kRetry);

        int totalAttempts = 0;
        ulong totalBurn = 0;
        ulong firstBurn = 0;
        int admitted = 0;
        AcceptTxResult lastAdmission = AcceptTxResult.Accepted;
        for (int residency = 0; residency < residencies; residency++)
        {
            lastAdmission = _pool!.SubmitTx(tx, TxHandlingOptions.None);
            if (lastAdmission != AcceptTxResult.Accepted) break;
            admitted++;

            (ProducerLoopResult run, CountingAdapter adapter) = await OfferUntilDropped(tx, kRetry, mPerHead);
            totalAttempts += run.Attempts;
            foreach (ulong b in adapter.BurnedPerAttempt) totalBurn += b;
            if (residency == 0 && adapter.BurnedPerAttempt.Count > 0) firstBurn = adapter.BurnedPerAttempt[0];
        }

        Emit($"case=resubmit_measured k_retry={kRetry} k_basis=measured m_per_head={mPerHead} m_basis=scheduled "
             + $"budget={verifyGas} residencies={residencies} readmitted={admitted} last_admission={lastAdmission} "
             + $"execution_attempts={totalAttempts} burn_first_attempt={firstBurn} burn_total={totalBurn} "
             + $"amplification={(firstBurn == 0 ? 0 : (double)totalBurn / firstBurn):F2} amplification_basis=measured "
             + $"modelled_attempts={residencies * perResidency}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(admitted, Is.EqualTo(residencies),
                $"a dropped frame transaction must be re-admittable, or the budget could not be refreshed by gossip; the pool answered {lastAdmission}");
            Assert.That(totalAttempts, Is.EqualTo(residencies * perResidency),
                "each residency must be granted a full budget again, which is what makes the burn unbounded in re-gossip");
        }
    }

    /// <summary>What one offer loop against the real pool observed.</summary>
    private readonly record struct ProducerLoopResult(
        int Attempts, int HeadsAdvanced, int MaxAttemptsOnOneHead, bool Dropped, List<double> Micros, double TotalMicros);

    /// <summary>
    /// Offers <paramref name="tx"/> to the production executor until the pool stops holding it, advancing the
    /// pool's chain head every <paramref name="mPerHead"/> offers.
    /// </summary>
    /// <remarks>The cap is one head beyond what the closed form predicts, so an over-run is reported as a failed
    /// assertion on the count rather than hidden by the loop bound.</remarks>
    private async Task<(ProducerLoopResult Run, CountingAdapter Adapter)> OfferUntilDropped(
        Transaction tx, int kRetry, int mPerHead)
    {
        int offerCap = kRetry * mPerHead + 1;

        BlockProcessor.BlockProductionTransactionsExecutor executor = BuildExecutor(_pool!, out CountingAdapter adapter);

        List<double> micros = [];
        double totalMicros = 0;
        int attempts = 0;
        int headsAdvanced = 0;
        int attemptsOnCurrentHead = 0;
        int maxAttemptsOnOneHead = 0;
        bool dropped = false;

        while (attempts < offerCap)
        {
            Block block = OfferBlock(executor, tx, _poolHeadNumber + 1, out TimeSpan elapsed);

            micros.Add(elapsed.TotalMicroseconds);
            totalMicros += elapsed.TotalMicroseconds;
            attempts++;
            attemptsOnCurrentHead++;
            if (attemptsOnCurrentHead > maxAttemptsOnOneHead) maxAttemptsOnOneHead = attemptsOnCurrentHead;

            Assert.That(block.Header.TxRoot, Is.EqualTo(Keccak.EmptyTreeHash),
                $"a prefix that never approves was built into block {attempts}");

            if (!_pool!.ContainsTx(tx.Hash!, tx.Type))
            {
                dropped = true;
                break;
            }

            if (attemptsOnCurrentHead >= mPerHead)
            {
                await AdvancePoolHead();
                headsAdvanced++;
                attemptsOnCurrentHead = 0;
            }
        }

        return (new ProducerLoopResult(attempts, headsAdvanced, maxAttemptsOnOneHead, dropped, micros, totalMicros), adapter);
    }

    /// <summary>Builds the pool the sweep measures, with the sender funded at the head it prices against.</summary>
    /// <remarks>Its state view is the pool's alone; the never-approving code the producer runs lives in the
    /// chain <see cref="BuildChain"/> built. <see cref="ITxPoolConfig.FrameTxMaxVerifyGas"/> is lifted so the
    /// sweep's declared budget is not what decides admission.</remarks>
    private void CreatePool(int kRetry)
    {
        TestReadOnlyStateProvider poolState = new();
        poolState.CreateAccount(Sender, UInt256.MaxValue);

        _poolHeadNumber = 1;
        _poolHeadTree = new PoolHeadTree { Head = BuildPoolHead() };
        _poolHeadTree.BestSuggestedHeader = _poolHeadTree.Head!.Header;

        _pool = new TxPool.TxPool(
            new EthereumEcdsa(_specProvider.ChainId),
            new BlobTxStorage(),
            new ChainHeadInfoProvider(new ChainHeadSpecProvider(_specProvider, _poolHeadTree), _poolHeadTree, poolState)
            {
                // The pool raises TxPoolHeadChanged only while it considers itself synced, and AdvancePoolHead
                // waits on that event, so pin it rather than leaving it to the pinned head's block number.
                HasSynced = true
            },
            new TxPoolConfig
            {
                GasLimit = BlockGasLimit,
                FrameTxMaxVerifyGas = 0,
                FrameTxEvictionRetryBudget = kRetry,
            },
            new TxValidator(_specProvider.ChainId),
            new SpecChangeTxValidator(_specProvider.ChainId),
            LimboLogs.Instance,
            new TransactionComparerProvider(_specProvider, _poolHeadTree).GetDefaultComparer(),
            ShouldGossip.Instance);
    }

    /// <remarks>The wait is bounded so that a pool that stops raising the event surfaces as a failed case
    /// rather than as a hung fixture.</remarks>
    private async Task AdvancePoolHead()
    {
        _poolHeadNumber++;
        Block block = BuildPoolHead();

        Task waitTask = Wait.ForEventCondition<Block>(
            CancellationToken.None,
            e => _pool!.TxPoolHeadChanged += e,
            e => _pool!.TxPoolHeadChanged -= e,
            e => e.Number == block.Number);

        _poolHeadTree.Head = block;
        _poolHeadTree.BestSuggestedHeader = block.Header;
        _poolHeadTree.RaiseBlockAddedToMain(new BlockReplacementEventArgs(block));
        await waitTask.WaitAsync(HeadChangeTimeout);
    }

    private Block BuildPoolHead() =>
        Build.A.Block
            .WithNumber(_poolHeadNumber)
            .WithBaseFeePerGas(UInt256.Zero)
            .WithGasLimit(BlockGasLimit)
            .TestObject;

    private static double Percentile(List<double> values, double quantile)
    {
        if (values.Count == 0) return double.NaN;
        List<double> sorted = [.. values];
        sorted.Sort();
        int rank = (int)Math.Ceiling(quantile * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    /// <summary>The pool's chain head, advanced by the sweep and by nothing else.</summary>
    private sealed class PoolHeadTree : BlockTreeTestDouble
    {
        public override BlockHeader FindBestSuggestedHeader() => BestSuggestedHeader!;
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
