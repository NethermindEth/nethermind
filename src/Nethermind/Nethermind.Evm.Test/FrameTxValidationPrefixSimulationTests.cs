// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
    private EthereumVirtualMachine _virtualMachine;
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
        _virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, _virtualMachine, codeInfoRepository, LimboLogs.Instance);
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
    public void Simulate_PrefixExceedsMaxVerifyGas_RejectedAsOverBudget()
    {
        // A frame declaring far more than MAX_VERIFY_GAS is capped to the remaining budget; an unbounded
        // loop then exhausts that cap. The rejection must be reported as over-budget, distinct from a
        // plain revert of a within-budget frame.
        byte[] code = Prepare.EvmCode.Op(Instruction.JUMPDEST).PushData(0).Op(Instruction.JUMP).Done;
        DeployContract(Sender, code, 1.Ether);
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 10_000_000, UInt256.Zero, default));

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.ErrorDescription, Does.Contain("MAX_VERIFY_GAS"));
            Assert.That(tracer.Payer, Is.Null);
        }
    }

    [Test]
    public void Simulate_PrefixCallsCodelessTarget_RecordsViolation()
    {
        // CALL*/EXTCODE* to an address that is neither an existing contract nor a precompile is banned:
        // its validity would depend on the target staying codeless — an unindexed mempool dependency
        // (EIP-8141 §Validation Trace Rules, L816). AddressC is never deployed, so it is codeless.
        byte[] code = Prepare.EvmCode
            .StaticCall(TestItem.AddressC, 50_000)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        DeployContract(Sender, code, 1.Ether);

        (_, FrameTxValidationTracer tracer) = SimulateAllowingAbort(FrameTx(nonce: 0, SelfVerifyFrame()));

        Assert.That(tracer.Violated, Is.True);
    }

    [Test]
    public void Simulate_PrefixCallsExistingContract_Allowed()
    {
        // CALL*/EXTCODE* to an existing (non-delegated) contract is permitted — helper contracts and
        // libraries may be used during validation (EIP-8141 §Validation Trace Rules, L853).
        DeployContract(TestItem.AddressC, Prepare.EvmCode.Op(Instruction.STOP).Done);
        byte[] code = Prepare.EvmCode
            .StaticCall(TestItem.AddressC, 50_000)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        DeployContract(Sender, code, 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Violated, Is.False);
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(tracer.Payer, Is.EqualTo(Sender));
        }
    }

    // The banned list is the security surface of admission, so it is swept whole: any of these in the
    // validation prefix rejects the transaction even though the frame goes on to call APPROVE.
    private static IEnumerable<TestCaseData> BannedOpcodes()
    {
        foreach (Instruction op in new[]
                 {
                     Instruction.ORIGIN, Instruction.GASPRICE, Instruction.BLOCKHASH, Instruction.COINBASE,
                     Instruction.TIMESTAMP, Instruction.NUMBER, Instruction.PREVRANDAO, Instruction.GASLIMIT,
                     Instruction.BASEFEE, Instruction.BLOBBASEFEE, Instruction.SELFBALANCE, Instruction.INVALID,
                 })
        {
            yield return new TestCaseData((byte)op, 0).SetName($"banned {op}");
        }

        foreach (Instruction op in new[] { Instruction.BALANCE, Instruction.BLOBHASH, Instruction.TLOAD, Instruction.SELFDESTRUCT })
        {
            yield return new TestCaseData((byte)op, 1).SetName($"banned {op}");
        }

        foreach (Instruction op in new[] { Instruction.SSTORE, Instruction.TSTORE })
        {
            yield return new TestCaseData((byte)op, 2).SetName($"banned {op}");
        }

        yield return new TestCaseData((byte)Instruction.CREATE, 3).SetName("banned CREATE");
        yield return new TestCaseData((byte)Instruction.CREATE2, 4).SetName("banned CREATE2");
        // EIP-7819; matched by raw byte rather than through the Instruction enum, on its own code path.
        yield return new TestCaseData((byte)0xf6, 3).SetName("banned SETDELEGATE");
    }

    [TestCaseSource(nameof(BannedOpcodes))]
    public void Simulate_PrefixUsesBannedOpcode_RecordsViolation(byte banned, int operands)
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < operands; i++) code = code.PushData(0);
        byte[] deployed = code.Op((Instruction)banned)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        DeployContract(Sender, deployed, 1.Ether);

        (_, FrameTxValidationTracer tracer) = SimulateAllowingAbort(FrameTx(nonce: 0, SelfVerifyFrame()));

        Assert.That(tracer.Violated, Is.True);
    }

    [TestCase(true, TestName = "GAS immediately before a call is permitted")]
    [TestCase(false, TestName = "bare GAS is banned")]
    public void Simulate_GasOpcode_OnlyPermittedBeforeACall(bool beforeCall)
    {
        DeployContract(TestItem.AddressC, Prepare.EvmCode.Op(Instruction.STOP).Done);
        Prepare code = Prepare.EvmCode;
        code = beforeCall
            // STATICCALL operands with GAS forwarding the remaining gas, the idiom the caveat permits.
            ? code.PushData(0).PushData(0).PushData(0).PushData(0).PushData(TestItem.AddressC)
                .Op(Instruction.GAS).Op(Instruction.STATICCALL).Op(Instruction.POP)
            : code.Op(Instruction.GAS).Op(Instruction.POP);
        DeployContract(Sender, code
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done, 1.Ether);

        (_, FrameTxValidationTracer tracer) = SimulateAllowingAbort(FrameTx(nonce: 0, SelfVerifyFrame()));

        Assert.That(tracer.Violated, Is.EqualTo(!beforeCall));
    }

    [Test]
    public void Simulate_SloadOfSenderStorage_Allowed()
    {
        byte[] code = Prepare.EvmCode
            .PushData(0).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        DeployContract(Sender, code, 1.Ether);

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(FrameTx(nonce: 0, SelfVerifyFrame()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Violated, Is.False);
            Assert.That(result.TransactionExecuted, Is.True);
        }
    }

    [Test]
    public void Simulate_SloadOutsideSenderStorage_RecordsViolation()
    {
        // A helper contract reading its own storage is the canonical code-carrying paymaster shape;
        // it adds a mutable-state dependency the pool cannot index, so it is rejected.
        DeployContract(TestItem.AddressC, Prepare.EvmCode.PushData(0).Op(Instruction.SLOAD).Op(Instruction.POP).Op(Instruction.STOP).Done);
        byte[] code = Prepare.EvmCode
            .StaticCall(TestItem.AddressC, 50_000).Op(Instruction.POP)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        DeployContract(Sender, code, 1.Ether);

        (_, FrameTxValidationTracer tracer) = SimulateAllowingAbort(FrameTx(nonce: 0, SelfVerifyFrame()));

        Assert.That(tracer.Violated, Is.True);
    }

    [Test]
    public void Simulate_TimestampInCanonicalExpiryVerifier_Allowed()
    {
        DeployContract(Eip8141Constants.ExpiryVerifierAddress, Eip8141Constants.ExpiryVerifierCode);
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        byte[] deadline = new byte[Eip8141Constants.ExpiryDataLength];
        BinaryPrimitives.WriteUInt64BigEndian(deadline, ulong.MaxValue);
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveScopeNone, Eip8141Constants.ExpiryVerifierAddress, gasLimit: 30_000, UInt256.Zero, deadline),
            SelfVerifyFrame());

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Violated, Is.False);
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(tracer.Payer, Is.EqualTo(Sender));
        }
    }

    [Test]
    public void Simulate_ExceedingWallClockBound_AbortsTheInterpreter()
    {
        // The gas bound alone prices a prefix by opcode, not by wall clock; the deadline is what caps
        // work the gas schedule underprices, and it aborts rather than merely recording.
        DeployContract(Sender, Prepare.EvmCode.Op(Instruction.JUMPDEST).PushData(0).Op(Instruction.JUMP).Done, 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        FrameTxValidationTracer tracer = Tracer(tx, TimeSpan.FromTicks(1));

        Assert.Throws<OperationCanceledException>(() => Run(tx, tracer));
        Assert.That(tracer.TimedOut, Is.True);
    }

    [Test]
    public void Simulate_PrefixStartsWithDeployFrame_RejectedAsUnsimulated()
    {
        // The recognized grammar allows a leading deploy frame, but its carve-outs are unimplemented, so
        // the prefix is declined before the frame is entered — not reported as "never set a payer".
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, TestItem.AddressC, gasLimit: 200_000, UInt256.Zero, default),
            SelfVerifyFrame());

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.ErrorDescription, Does.Contain("deploy frame"));
            Assert.That(tracer.Violated, Is.False);
            Assert.That(tracer.Payer, Is.Null);
        }
    }

    [Test]
    public void Simulate_AbortedInsideAChildFrame_ReleasesTheUnwoundFrames()
    {
        // The helper violates the SLOAD scope rule and then spins, so the abort fires on a later poll,
        // inside the child frame. The interpreter has to release the frames it unwound past, or their
        // pooled data stacks stay rooted for the lifetime of the reused machine.
        DeployContract(TestItem.AddressC, Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(0).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(0).Op(Instruction.JUMP).Done);
        byte[] code = Prepare.EvmCode
            .StaticCall(TestItem.AddressC, 50_000).Op(Instruction.POP)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        DeployContract(Sender, code, 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        FrameTxValidationTracer tracer = Tracer(tx);

        Assert.Throws<OperationCanceledException>(() => Run(tx, tracer));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Violated, Is.True);
            Assert.That(_virtualMachine.StateStack, Is.Empty);
        }
    }

    /// <summary>Runs a prefix the tracer may abort mid-execution, returning the tracer either way.</summary>
    private (TransactionResult, FrameTxValidationTracer) SimulateAllowingAbort(Transaction tx)
    {
        FrameTxValidationTracer tracer = Tracer(tx);
        try
        {
            return (Run(tx, tracer), tracer);
        }
        catch (OperationCanceledException)
        {
            return (default, tracer);
        }
    }

    private (TransactionResult, FrameTxValidationTracer) Simulate(Transaction tx)
    {
        FrameTxValidationTracer tracer = Tracer(tx);
        return (Run(tx, tracer), tracer);
    }

    private FrameTxValidationTracer Tracer(Transaction tx, TimeSpan timeout = default) =>
        new(tx.SenderAddress!, Eip8141Constants.ExpiryVerifierAddress, _stateProvider, Spec, default, timeout);

    private TransactionResult Run(Transaction tx, FrameTxValidationTracer tracer)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Spec));
        return _transactionProcessor.Process(tx, tracer, ExecutionOptions.FrameValidationPrefixOnly);
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
