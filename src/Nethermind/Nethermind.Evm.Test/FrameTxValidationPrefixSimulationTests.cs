// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-8141 in-pool validation-prefix simulation (Phase 2): exercises the processor's
/// <see cref="ExecutionOptions.FrameValidationPrefixOnly"/> path together with the
/// <see cref="FrameTxValidationTracer"/> — resolving the payer of an opaque (deployed-code) prefix,
/// halting once the payer is set, enforcing the trace/opcode rules, and bounding gas by MAX_VERIFY_GAS.
/// </summary>
[TestFixture]
public class FrameTxValidationPrefixSimulationTests
{
    private ISpecProvider _specProvider;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private IDisposable _worldStateCloser;
    private IReleaseSpec Spec => _specProvider.GenesisSpec;

    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Sponsor = TestItem.AddressD;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Eip8141Prototype.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _worldStateCloser?.Dispose();

    [Test]
    public void Simulate_DeployedCodeSenderApprovesExecutionAndPayment_ResolvesSenderAsPayer()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(tracer.Violated, Is.False);
            Assert.That(tracer.Payer, Is.EqualTo(Sender));
            // The simulation must not mutate canonical state.
            Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(0UL));
            Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether));
        }
    }

    [Test]
    public void Simulate_DeployedCodeSponsorPaysAfterExecutionApproval_ResolvesSponsorAsPayer()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecution), 1.Ether);
        DeployContract(Sponsor, ApproveCode(TxFrame.ApprovePayment), 1.Ether);
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApprovePayment, Sponsor, gasLimit: 200_000, UInt256.Zero, default),
            // An execution frame after the prefix: simulation must halt before reaching it.
            new TxFrame(TxFrame.ModeSender, flags: 0, target: TestItem.AddressC, gasLimit: 200_000, UInt256.Zero, default));

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(tracer.Violated, Is.False);
            Assert.That(tracer.Payer, Is.EqualTo(Sponsor));
        }
    }

    [Test]
    public void Simulate_PrefixNeverSetsPayer_Rejected()
    {
        // A VERIFY frame that returns without calling APPROVE leaves the payer unset.
        DeployContract(Sender, Prepare.EvmCode.Op(Instruction.STOP).Done, 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(tracer.Payer, Is.Null);
        }
    }

    [Test]
    public void Simulate_PrefixReverts_Rejected()
    {
        DeployContract(Sender, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done, 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(tracer.Payer, Is.Null);
        }
    }

    [Test]
    public void Simulate_PrefixUsesBannedOpcode_RecordsViolation()
    {
        // TIMESTAMP is banned during the validation prefix outside the expiry verifier frame, even
        // though the frame still calls APPROVE and would otherwise resolve a payer.
        byte[] code = Prepare.EvmCode
            .Op(Instruction.TIMESTAMP).Op(Instruction.POP)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        DeployContract(Sender, code, 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        (_, FrameTxValidationTracer tracer) = Simulate(tx);

        Assert.That(tracer.Violated, Is.True);
    }

    [Test]
    public void Simulate_PrefixExceedsMaxVerifyGas_Rejected()
    {
        // An unbounded loop consumes more than MAX_VERIFY_GAS; the gas cap forces an out-of-gas
        // revert before any APPROVE, so the prefix is rejected rather than run to completion.
        byte[] code = Prepare.EvmCode.Op(Instruction.JUMPDEST).PushData(0).Op(Instruction.JUMP).Done;
        DeployContract(Sender, code, 1.Ether);
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 10_000_000, UInt256.Zero, default));

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(tracer.Payer, Is.Null);
        }
    }

    private (TransactionResult, FrameTxValidationTracer) Simulate(Transaction tx)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        FrameTxValidationTracer tracer = new(tx.SenderAddress!, Eip8141Constants.ExpiryVerifierAddress);
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Spec));
        TransactionResult result = _transactionProcessor.Process(tx, tracer, ExecutionOptions.FrameValidationPrefixOnly);
        return (result, tracer);
    }

    private void DeployContract(Address address, byte[] code, UInt256 balance = default)
    {
        _stateProvider.CreateAccount(address, balance);
        _stateProvider.InsertCode(address, code, Spec);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
    }

    private static byte[] ApproveCode(byte scope) =>
        Prepare.EvmCode.PushData(scope).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;

    private static TxFrame SelfVerifyFrame() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default);

    private static Transaction FrameTx(ulong nonce, params TxFrame[] frames) =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = nonce,
            SenderAddress = Sender,
            Frames = frames,
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };
}
