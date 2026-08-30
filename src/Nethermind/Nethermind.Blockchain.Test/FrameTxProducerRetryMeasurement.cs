// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Core.Test.Builders;
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

    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Beneficiary = TestItem.AddressE;

    private ISpecProvider _specProvider = null!;
    private IWorldState _stateProvider = null!;
    private ITransactionProcessor _transactionProcessor = null!;
    private IDisposable _stateCloser = null!;

    private IReleaseSpec Spec => _specProvider.GenesisSpec;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Eip8141Prototype.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _stateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _stateCloser?.Dispose();

    /// <summary>A prefix that never approves: it loops until the frame's gas limit is exhausted.</summary>
    private static byte[] NeverApproves() =>
        Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done;

    private static byte[] Approves() =>
        Prepare.EvmCode
            .PushData(TxFrame.ApproveExecutionAndPayment)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.APPROVE)
            .Done;

    [TestCase(true, 236_285ul, TestName = "control: a prefix that approves is included and paid for")]
    [TestCase(false, 300_000ul, TestName = "never approves, at the default MAX_VERIFY_GAS")]
    [TestCase(false, 236_285ul, TestName = "never approves, at a measured private-pool prefix")]
    public void ProducerRetriesAFailingPrefix(bool approves, ulong verifyGas)
    {
        _stateProvider.CreateAccount(Sender, 100.Ether);
        _stateProvider.InsertCode(Sender, approves ? Approves() : NeverApproves(), Spec);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

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
                Assert.That(firstBurn, Is.GreaterThan(verifyGas * 99 / 100), "the whole verification budget must be burned per attempt");
                Assert.That(beneficiaryDelta, Is.EqualTo(UInt256.Zero), "the work was paid for after all");
                Assert.That(senderDelta, Is.EqualTo(UInt256.Zero), "the sender was charged after all");
            }
        }
    }

    /// <summary>
    /// The campaign's <c>K_retry</c> sweep: how much unpaid verification work a never-approving prefix
    /// extracts from a producer that evicts it after <paramref name="kRetry"/> failed build attempts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>K_retry</c> is a client-policy parameter, not a spec rule — <c>ethereum/EIPs#12213</c> proposed a
    /// normative producer bound and was reframed as non-normative guidance to match this sweep. Production
    /// today is effectively <c>K_retry = 1</c>: <c>EvictUnpaidFrameTx</c> asks the pool to evict on the first
    /// <c>MalformedTransaction</c> result. This sweep is what makes the other values measurable without
    /// touching production code.
    /// </para>
    /// <para>
    /// The gate stands in for the pool's eviction decision, which is the only thing that ends the retry
    /// series: nothing else removes a transaction whose prefix never approves, because it never pays and so
    /// never advances its nonce. Once the gate reports the transaction evicted, the loop stops offering it,
    /// which is what a real pool would do by no longer returning it to the producer.
    /// </para>
    /// <para>
    /// <see cref="ProducerRetriesAFailingPrefix"/> passes <c>NullTxPool.Instance</c>, whose
    /// <c>EvictTransaction</c> always returns <c>false</c>, so it measures the unbounded case — the same
    /// shape as this sweep with no eviction at all. Read the two together: that case is the ceiling this one
    /// bounds.
    /// </para>
    /// </remarks>
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(8)]
    public void ProducerRetriesAreBoundedByKRetry(int kRetry)
    {
        const ulong VerifyGas = 236_285;

        _stateProvider.CreateAccount(Sender, 100.Ether);
        _stateProvider.InsertCode(Sender, NeverApproves(), Spec);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        CountingAdapter adapter = new(new BuildUpTransactionProcessorAdapter(_transactionProcessor));
        BlockProcessor.BlockProductionTransactionPicker picker = new(_specProvider);
        IBlockAccessListManager balManager = Substitute.For<IBlockAccessListManager>();
        balManager.Enabled.Returns(false);

        // Stands in for the pool's eviction decision: the transaction survives kRetry failed attempts and is
        // evicted on the kRetry-th, which is what the plan's K_retry counts.
        int evictionRequests = 0;
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.EvictTransaction(Arg.Any<Transaction>()).Returns(_ => ++evictionRequests >= kRetry);

        BlockProcessor.BlockProductionTransactionsExecutor executor =
            new(adapter, _stateProvider, picker, LimboLogs.Instance, balManager, txPool);

        Transaction tx = FrameTx(VerifyGas);
        UInt256 beneficiaryBefore = _stateProvider.GetBalance(Beneficiary);

        int blocksOffered = 0;
        for (int i = 0; i < Attempts && evictionRequests < kRetry; i++)
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
            blocksOffered++;

            if (block.Header.TxRoot != Keccak.EmptyTreeHash)
            {
                Assert.Fail($"a prefix that never approves was built into block {1 + i}");
            }
        }

        ulong burned = 0;
        foreach (ulong b in adapter.BurnedPerAttempt) burned += b;
        ulong firstBurn = adapter.BurnedPerAttempt.Count > 0 ? adapter.BurnedPerAttempt[0] : 0;

        Emit($"case=k_retry_sweep k_retry={kRetry} budget={VerifyGas} "
             + $"blocks_offered={blocksOffered} execution_attempts={adapter.Attempts} "
             + $"burn_first_attempt={firstBurn} burn_total={burned} "
             + $"amplification={(firstBurn == 0 ? 0 : (double)burned / firstBurn):F2} "
             + $"beneficiary_delta={_stateProvider.GetBalance(Beneficiary) - beneficiaryBefore}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(adapter.Attempts, Is.EqualTo(kRetry),
                "the producer must re-execute the prefix exactly K_retry times before the pool evicts it");
            Assert.That(firstBurn, Is.GreaterThan(VerifyGas * 99 / 100),
                "each attempt must burn the whole verification budget, or the amplification is measured against the wrong unit");
            Assert.That(burned, Is.GreaterThan(firstBurn * (ulong)kRetry * 99 / 100),
                "total unpaid burn must scale with K_retry, which is the quantity this sweep exists to report");
            Assert.That(_stateProvider.GetBalance(Beneficiary) - beneficiaryBefore, Is.EqualTo(UInt256.Zero),
                "the work was paid for after all, so it is not unpaid burn");
        }
    }

    private static Transaction FrameTx(ulong verifyGas)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Sender,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: verifyGas, UInt256.Zero, default)],
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
