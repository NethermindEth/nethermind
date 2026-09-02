// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>EIP-8141 in-pool validation-prefix simulation: the processor's
/// <see cref="ExecutionOptions.FrameValidationPrefixOnly"/> path and <see cref="FrameTxValidationTracer"/>.</summary>
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
    private static readonly Address Factory = TestItem.AddressB;
    private static readonly byte[] Salt = new byte[32];

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

    [TestCase(Instruction.ORIGIN)]
    [TestCase(Instruction.BLOBHASH)]
    [TestCase(Instruction.TLOAD)]
    public void Simulate_PrefixUsesRelaxedOpcode_ResolvesPayer(Instruction opcode)
    {
        // Each reads the frame or transaction payload, not the block environment, so none makes the
        // prefix depend on state that could differ between simulation and inclusion.
        byte[] probe = Prepare.EvmCode.PushData(0).Op(opcode).Op(Instruction.POP).Done;
        DeployContract(Sender, [.. probe, .. ApproveCode(TxFrame.ApproveExecutionAndPayment)], 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(tracer.Violated, Is.False);
            Assert.That(tracer.Payer, Is.EqualTo(Sender));
        }
    }

    [Test]
    public void Simulate_PrefixUsesAnUndefinedOpcode_RejectedByTheBadInstructionHalt()
    {
        // 0xF6 is undefined on every fork we ship, so the EVM's own halt fails the prefix and the tracer
        // needs no rule for it.
        byte[] code = [0xf6, .. ApproveCode(TxFrame.ApproveExecutionAndPayment)];
        DeployContract(Sender, code, 1.Ether);
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
        // A capped frame that then runs out of gas must report as over-budget, not as a plain revert.
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
        // Validity would otherwise depend on the target staying codeless — an unindexed dependency.
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
        // Helper contracts and libraries may be used during validation.
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
                     Instruction.GASPRICE, Instruction.BLOCKHASH, Instruction.COINBASE,
                     Instruction.TIMESTAMP, Instruction.NUMBER, Instruction.PREVRANDAO, Instruction.GASLIMIT,
                     Instruction.BASEFEE, Instruction.BLOBBASEFEE, Instruction.SLOTNUM, Instruction.SELFBALANCE,
                     Instruction.INVALID,
                 })
        {
            yield return new TestCaseData((byte)op, 0).SetName($"banned {op}");
        }

        foreach (Instruction op in new[] { Instruction.BALANCE, Instruction.SELFDESTRUCT })
        {
            yield return new TestCaseData((byte)op, 1).SetName($"banned {op}");
        }

        yield return new TestCaseData((byte)Instruction.SSTORE, 2).SetName("banned SSTORE");

        yield return new TestCaseData((byte)Instruction.CREATE, 3).SetName("banned CREATE");
        yield return new TestCaseData((byte)Instruction.CREATE2, 4).SetName("banned CREATE2");
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
    public void Simulate_DeployFrameInstallsCodeAtTheSender_ResolvesTheDeployedAccountAsPayer()
    {
        byte[] initCode = Prepare.EvmCode.ForInitOf(ApproveCode(TxFrame.ApproveExecutionAndPayment)).Done;
        Address deployed = InstallFactory(initCode);
        FundAccount(deployed, 1.Ether);
        Transaction tx = DeployTx(deployed);

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.ViolationReason, Is.Null);
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(tracer.Payer, Is.EqualTo(deployed));
            // The simulation must not leave the deployment behind.
            Assert.That(_stateProvider.IsContract(deployed), Is.False);
        }
    }

    [Test]
    public void Simulate_DeployFrameInstallsCodeAwayFromTheSender_RecordsViolation()
    {
        // The carve-out covers code installed at tx.sender only; the sender already carrying code
        // leaves the created address as the sole thing under test.
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        InstallFactory(Prepare.EvmCode.ForInitOf(Prepare.EvmCode.Op(Instruction.STOP).Done).Done);
        Transaction tx = FrameTx(nonce: 0, DeployFrame(), SelfVerifyFrame());

        (_, FrameTxValidationTracer tracer) = SimulateAllowingAbort(tx);

        Assert.That(tracer.ViolationReason, Does.Contain("CREATE outside tx.sender"));
    }

    [Test]
    public void Simulate_DeployFrameStoresToTheSenderStorage_Allowed()
    {
        byte[] initCode = Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.SSTORE)
            .ForInitOf(ApproveCode(TxFrame.ApproveExecutionAndPayment)).Done;
        Address deployed = InstallFactory(initCode);
        FundAccount(deployed, 1.Ether);
        Transaction tx = DeployTx(deployed);

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.ViolationReason, Is.Null);
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(tracer.Payer, Is.EqualTo(deployed));
        }
    }

    [Test]
    public void Simulate_DeployFrameStoresToTheFactoryStorage_RecordsViolation()
    {
        // Per-deploy factory storage would make the deployment depend on chain state.
        byte[] initCode = Prepare.EvmCode.ForInitOf(ApproveCode(TxFrame.ApproveExecutionAndPayment)).Done;
        byte[] prologue = Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Done;
        Address deployed = InstallFactory(initCode, prologue);
        FundAccount(deployed, 1.Ether);
        Transaction tx = DeployTx(deployed);

        (_, FrameTxValidationTracer tracer) = SimulateAllowingAbort(tx);

        Assert.That(tracer.ViolationReason, Does.Contain("SSTORE outside tx.sender storage"));
    }

    [Test]
    public void Simulate_DeployFrameCreatesOverAnExistingAccount_RecordsViolation()
    {
        // A create that opens no frame returned zero on a collision the prefix must not turn on —
        // here a front-run of the very deployment the frame intends.
        byte[] initCode = Prepare.EvmCode.ForInitOf(ApproveCode(TxFrame.ApproveExecutionAndPayment)).Done;
        Address deployed = InstallFactory(initCode);
        DeployContract(deployed, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        Transaction tx = DeployTx(deployed);

        (_, FrameTxValidationTracer tracer) = SimulateAllowingAbort(tx);

        Assert.That(tracer.ViolationReason, Does.Contain("CREATE opened no creation frame"));
    }

    [Test]
    public void Simulate_VerifyFrameCreatesAContract_RecordsViolation()
    {
        // The carve-out belongs to the opening deploy frame alone.
        byte[] code = Prepare.EvmCode
            .Create(Prepare.EvmCode.Op(Instruction.STOP).Done, 0).Op(Instruction.POP)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        DeployContract(Sender, code, 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        (_, FrameTxValidationTracer tracer) = Simulate(tx);

        Assert.That(tracer.ViolationReason, Does.Contain("banned opcode CREATE"));
    }

    [TestCase(false, TestName = "an undelegated factory is allowed")]
    [TestCase(true, TestName = "a delegated factory is refused")]
    public void Simulate_DeployFrameTargetingADelegatedFactory_RecordsViolation(bool delegated)
    {
        // The processor dispatches the deploy frame's target, so it never meets the CALL* target rule; a
        // delegated factory is mutable by its authority, which is what that rule exists to close.
        byte[] initCode = Prepare.EvmCode.ForInitOf(ApproveCode(TxFrame.ApproveExecutionAndPayment)).Done;
        Address deployed = InstallFactory(initCode);
        FundAccount(deployed, 1.Ether);
        if (delegated)
        {
            DeployContract(TestItem.AddressC, Prepare.EvmCode.Op(Instruction.STOP).Done);
            byte[] delegation = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressC.Bytes];
            _stateProvider.InsertCode(Factory, delegation, Spec);
            _stateProvider.Commit(Spec);
            _stateProvider.CommitTree(0);
        }

        (_, FrameTxValidationTracer tracer) = SimulateAllowingAbort(DeployTx(deployed));

        Assert.That(tracer.ViolationReason, delegated
            ? Does.Contain("is not an undelegated contract")
            : Is.Null);
    }

    // Nothing journals the code cache, so a deposit outlives the rollback that discards the prefix. Over the
    // process-wide instance that lets a peer fill the cache block processing reads from, for code never deployed.
    [TestCase(false, TestName = "an accepted prefix deposits into the cache")]
    [TestCase(true, TestName = "a prefix rejected after the create deposits too")]
    public void Simulate_DeployFrameDeposit_EscapesTheRollbackIntoTheCodeCache(bool violates)
    {
        // The rejected case violates in the VERIFY frame behind the deploy frame, since the tracer aborts
        // the interpreter at the first violation and one before the create would stop it happening at all.
        byte[] deployedCode = violates
            ? Prepare.EvmCode.Op(Instruction.SELFBALANCE).Op(Instruction.POP)
                .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done
            : ApproveCode(TxFrame.ApproveExecutionAndPayment);
        byte[] initCode = Prepare.EvmCode.ForInitOf(deployedCode).Done;
        Address deployed = InstallFactory(initCode);
        FundAccount(deployed, 1.Ether);
        ValueHash256 depositedHash = Keccak.Compute(deployedCode).ValueHash256;

        StaticCodeCache given = new(MemoryAllowance.CodeCacheSize);
        StaticCodeCache other = new(MemoryAllowance.CodeCacheSize);

        FrameTxValidationTracer tracer = RunUnder(given, DeployTx(deployed));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.Violated, Is.EqualTo(violates), "the case must reject for the reason it claims");
            // Where it rejects is the property the shape exists for: after the create, not before it.
            Assert.That(tracer.ViolationReason, violates
                ? Does.Contain("banned opcode SELFBALANCE")
                : Is.Null);
            // Contained rather than suppressed: the prefix keeps its read memoization, and the deposit is
            // confined to the env's own instance — which is what wiring the simulator away from the
            // process-wide one buys, since nothing journals either.
            Assert.That(given.Get(in depositedHash), Is.Not.Null, "a deposit lands in the cache the env was given");
            Assert.That(other.Get(in depositedHash), Is.Null, "and in no other, which is what the isolation rests on");
        }
    }

    private FrameTxValidationTracer RunUnder(ICodeCache codeCache, Transaction tx)
    {
        CacheCodeInfoRepository repository = new(_stateProvider, new EthereumPrecompileProvider(), codeCache);
        EthereumVirtualMachine machine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        ITransactionProcessor processor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, machine, repository, LimboLogs.Instance);
        Block block = Build.A.Block.WithNumber(1).WithBaseFeePerGas(0).WithTransactions(tx).WithGasLimit(30_000_000).TestObject;
        FrameTxValidationTracer tracer = new(tx.SenderAddress!, Eip8141Constants.ExpiryVerifierAddress, _stateProvider, Spec);
        processor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Spec));
        try
        {
            processor.Process(tx, tracer, ExecutionOptions.FrameValidationPrefixOnly);
        }
        catch (OperationCanceledException)
        {
            // The tracer aborts on the violation this test deliberately provokes; the deposit already ran.
        }

        return tracer;
    }

    // CREATE2's address is f(factory, salt, initcode); plain CREATE's is f(factory, factory.nonce), which any
    // third party can move by making the factory create again, leaving tx.sender codeless after admission.
    [Test]
    public void Simulate_DeployFrameUsingPlainCreate_RecordsViolation()
    {
        byte[] initCode = Prepare.EvmCode.ForInitOf(ApproveCode(TxFrame.ApproveExecutionAndPayment)).Done;
        DeployContract(Factory, Prepare.EvmCode.Create(initCode, 0).Done);
        Address deployed = ContractAddress.From(Factory, 0);
        FundAccount(deployed, 1.Ether);

        (_, FrameTxValidationTracer tracer) = SimulateAllowingAbort(DeployTx(deployed));

        Assert.That(tracer.ViolationReason, Does.Contain("banned opcode CREATE"));
    }

    // The endowment is the same one-bit dependency on the factory's balance that the funded-CALL ban closes:
    // underfunded, the create pushes zero and installs nothing, and anyone can drain the factory.
    [TestCase(0, false, TestName = "an unendowed CREATE2 is allowed")]
    [TestCase(1, true, TestName = "an endowed CREATE2 is refused")]
    public void Simulate_DeployFrameCreateEndowment_IsRefused(int endowment, bool violates)
    {
        byte[] initCode = Prepare.EvmCode.ForInitOf(ApproveCode(TxFrame.ApproveExecutionAndPayment)).Done;
        DeployContract(Factory, Prepare.EvmCode.Create2(initCode, Salt, (UInt256)endowment).Done, 1.Ether);
        Address deployed = ContractAddress.From(Factory, Salt, initCode);
        FundAccount(deployed, 1.Ether);

        (_, FrameTxValidationTracer tracer) = SimulateAllowingAbort(DeployTx(deployed));

        Assert.That(tracer.ViolationReason, violates ? Does.Contain("endowed CREATE2") : Is.Null);
    }

    [Test]
    public void Simulate_DeployFrameLeavesTheSenderCodeless_Rejected()
    {
        // Without code at tx.sender the VERIFY frames behind the deploy frame would validate against
        // default code instead of the account being deployed.
        DeployContract(Factory, Prepare.EvmCode.Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, DeployFrame(), SelfVerifyFrame());

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.ErrorDescription, Does.Contain("installed no code at tx.sender"));
            Assert.That(tracer.Payer, Is.Null);
        }
    }

    // The deploy frame is the one non-static prefix frame, so it is the only place a funded CALL is
    // reachable at all: it executes or pushes zero on the caller's balance, which a third party can move.
    [TestCase(0, false, TestName = "Simulate_DeployFrameMakesAZeroValueCall_Allowed")]
    [TestCase(1, true, TestName = "Simulate_DeployFrameMakesAValueCarryingCall_RecordsViolation")]
    public void Simulate_DeployFrameCallCarryingValue_IsRefused(int value, bool violates)
    {
        DeployContract(TestItem.AddressC, Prepare.EvmCode.Op(Instruction.STOP).Done);
        byte[] initCode = Prepare.EvmCode.ForInitOf(ApproveCode(TxFrame.ApproveExecutionAndPayment)).Done;
        byte[] prologue = Prepare.EvmCode.CallWithValue(TestItem.AddressC, 50_000, (UInt256)value).Op(Instruction.POP).Done;
        Address deployed = InstallFactory(initCode, prologue);
        FundAccount(deployed, 1.Ether);
        Transaction tx = DeployTx(deployed);

        (_, FrameTxValidationTracer tracer) = SimulateAllowingAbort(tx);

        Assert.That(tracer.ViolationReason, violates ? Does.Contain("value-carrying CALL") : Is.Null);
    }

    [Test]
    public void Simulate_DeployFrameBehindAnExpiryVerifyFrame_StillCarriesTheCarveOuts()
    {
        // OpensDeployPrefix accepts index 1 past an expiry-verify frame, and the SSTORE at tx.sender is
        // what proves the carve-outs were announced for that frame rather than only for index 0.
        DeployContract(Eip8141Constants.ExpiryVerifierAddress, Eip8141Constants.ExpiryVerifierCode);
        byte[] initCode = Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.SSTORE)
            .ForInitOf(ApproveCode(TxFrame.ApproveExecutionAndPayment)).Done;
        Address deployed = InstallFactory(initCode);
        FundAccount(deployed, 1.Ether);
        Transaction tx = FrameTx(nonce: 0, ExpiryVerifyFrame(), DeployFrame(), SelfVerifyFrame());
        tx.SenderAddress = deployed;

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.ViolationReason, Is.Null);
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(tracer.Payer, Is.EqualTo(deployed));
        }
    }

    [Test]
    public void Simulate_DeployFrameInstallingNothingOverADeployedSender_ResolvesThePayer()
    {
        // The guard is that tx.sender carries code once the deploy frame is done, so a deploy frame that
        // creates nothing passes it vacuously when the sender is already deployed. That is intended: the
        // VERIFY frames behind it run the sender's real code either way.
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        DeployContract(Factory, Prepare.EvmCode.Op(Instruction.STOP).Done);
        Transaction tx = FrameTx(nonce: 0, DeployFrame(), SelfVerifyFrame());

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.ViolationReason, Is.Null);
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(tracer.Payer, Is.EqualTo(Sender));
        }
    }

    [Test]
    public void Simulate_AbortedInsideAChildFrame_ReleasesTheUnwoundFrames()
    {
        // The abort fires inside the child frame, so the interpreter has to release the frames it unwound
        // past or their pooled data stacks stay rooted for the lifetime of the reused machine.
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
            // The release-mode tripwire: the interpreter's own Debug.Assert is compiled out of every CI job.
            Assert.That(StateStack().Count, Is.Zero);
        }
    }

    /// <summary>The interpreter's call-frame stack, which only a clean unwind leaves empty.</summary>
    private VmStateStack<EthereumGasPolicy> StateStack() =>
        (VmStateStack<EthereumGasPolicy>)typeof(VirtualMachine<EthereumGasPolicy>)
            .GetField("_stateStack", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_virtualMachine)!;

    [Test]
    public void Simulate_PrefixDeclaringAnUncommittedRecentRootReference_RejectedBeforeAnyFrameRuns()
    {
        // RECENTROOTREFLOAD reads the envelope on the strength of the pre-state check, so a prefix must
        // not run against references the main path would reject.
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.RecentRootReferences = [new RecentRootReference(TestItem.KeccakA, slot: 9, TestItem.KeccakB)];

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx, slotNumber: 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.ErrorDescription, Does.Contain("recent root reference"));
            Assert.That(tracer.Payer, Is.Null, "the prefix must not have resolved a payer");
        }
    }

    [Test]
    public void Simulate_DeployFrameAfterANonExpiryVerifyFrame_NotClaimedAsTheDeployGap()
    {
        // RecognizedPrefixLength reaches index 1 only past an expiry-verify frame, so a deploy frame
        // behind any other frame is not the shape the decline describes.
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecution), 1.Ether);
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, TestItem.AddressC, gasLimit: 200_000, UInt256.Zero, default),
            SelfVerifyFrame());

        (TransactionResult result, _) = Simulate(tx);

        Assert.That(result.ErrorDescription, Does.Not.Contain("deploy frame"));
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

    [TestCase(0, true, TestName = "a payer covering the EIP-7623 floor resolves")]
    [TestCase(-1, false, TestName = "a payer one wei short of the EIP-7623 floor does not")]
    public void Simulate_PricesTheApproveGateOnTheSameBudgetExecutionEscrows(int balanceDelta, bool resolves)
    {
        // A calldata-heavy prefix prices on the EIP-7623 floor, so the simulated APPROVE gate must use
        // the budget the main path escrows on.
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 30_000, UInt256.Zero,
                CalldataOf(30_000)));
        Assert.That(FrameTxValidation.TryCalculateGasBudget(tx, Spec, out _, out ulong floorGas, out ulong maxGas), Is.True);
        Assert.That(maxGas, Is.EqualTo(floorGas), "the fixture must be a shape where the floor binds");

        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), (UInt256)(maxGas + (ulong)(long)balanceDelta));

        (_, FrameTxValidationTracer tracer) = Simulate(tx);

        Assert.That(tracer.Payer, resolves ? Is.EqualTo(Sender) : Is.Null);
    }

    [Test]
    public void Simulate_UnpaidPrefixFollowedByAnExecutionFrame_RejectedAsNeverSettingAPayer()
    {
        // A scope-less DEFAULT frame is also the ordinary execution frame, so the deploy decline must
        // not claim this permanently-invalid shape.
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecution), 1.Ether);
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, TestItem.AddressC, gasLimit: 200_000, UInt256.Zero, default));

        (TransactionResult result, _) = Simulate(tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False);
            Assert.That(result.ErrorDescription, Does.Contain("never set a payer"));
        }
    }

    [Test]
    public void Simulate_PrefixCarriesAMismatchedSigner_RejectedByTheSignatureCheck()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.FrameSignatures = [MismatchedSignerSignature()];

        (TransactionResult result, _) = Simulate(tx);

        Assert.That(result.ErrorDescription, Is.EqualTo(FrameTxSignatureValidator.InvalidSecp256k1Signer));
    }

    [Test]
    public void Simulate_CallerAssertsSignaturesPreValidated_SkipsTheDuplicateCheckAndResolvesThePayer()
    {
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.FrameSignatures = [MismatchedSignerSignature()];

        (TransactionResult result, FrameTxValidationTracer tracer) = Simulate(tx, extraOptions: ExecutionOptions.FrameSignaturesPreValidated);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
            Assert.That(tracer.Payer, Is.EqualTo(Sender));
        }
    }

    [Test]
    public void Process_MainPathWithSignaturesPreValidated_VerifiesTheSignaturesAnyway()
    {
        // The assertion is only legible to the prefix simulation, so a caller that sets it on any other
        // path — block execution, eth_call, eth_estimateGas, eth_simulate, debug_traceCall — gains nothing.
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());
        tx.FrameSignatures = [MismatchedSignerSignature()];
        Block block = Build.A.Block.WithNumber(1).WithBaseFeePerGas(0).WithTransactions(tx).WithGasLimit(30_000_000).TestObject;
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Spec));

        TransactionResult result = _transactionProcessor.Process(tx, NullTxTracer.Instance, ExecutionOptions.FrameSignaturesPreValidated);

        Assert.That(result.ErrorDescription, Is.EqualTo(FrameTxSignatureValidator.InvalidSecp256k1Signer));
    }

    /// <summary>A canonical SECP256K1 entry that recovers to an address other than the declared signer.</summary>
    /// <remarks><c>1^3 + 7</c> is a quadratic residue, so <c>r = 1</c> recovers rather than failing verification.</remarks>
    private static TxFrameSignature MismatchedSignerSignature()
    {
        byte[] raw = new byte[TxFrameSignature.Secp256k1SignatureLength];
        raw[32] = 1; // r = 1
        raw[64] = 1; // s = 1
        return new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressB, default, raw);
    }

    private static byte[] CalldataOf(int length)
    {
        byte[] data = new byte[length];
        data.AsSpan().Fill(0xff);
        return data;
    }

    private (TransactionResult, FrameTxValidationTracer) Simulate(Transaction tx, ulong? slotNumber = null, ExecutionOptions extraOptions = ExecutionOptions.None)
    {
        FrameTxValidationTracer tracer = Tracer(tx);
        return (Run(tx, tracer, slotNumber, extraOptions), tracer);
    }

    private FrameTxValidationTracer Tracer(Transaction tx, TimeSpan timeout = default) =>
        new(tx.SenderAddress!, Eip8141Constants.ExpiryVerifierAddress, _stateProvider, Spec, default, timeout);

    private TransactionResult Run(Transaction tx, FrameTxValidationTracer tracer, ulong? slotNumber = null, ExecutionOptions extraOptions = ExecutionOptions.None)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithTransactions(tx)
            .WithSlotNumber(slotNumber)
            .WithGasLimit(30_000_000).TestObject;
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Spec));
        return _transactionProcessor.Process(tx, tracer, ExecutionOptions.FrameValidationPrefixOnly | extraOptions);
    }

    private void DeployContract(Address address, byte[] code, UInt256 balance = default)
    {
        _stateProvider.CreateAccount(address, balance);
        _stateProvider.InsertCode(address, code, Spec);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
    }

    private void FundAccount(Address address, UInt256 balance)
    {
        _stateProvider.CreateAccount(address, balance);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
    }

    /// <summary>Installs a CREATE2 factory for <paramref name="initCode"/> and returns the address it deploys to.</summary>
    private Address InstallFactory(byte[] initCode, byte[]? prologue = null)
    {
        DeployContract(Factory, [.. prologue ?? [], .. Prepare.EvmCode.Create2(initCode, Salt, 0).Done]);
        return ContractAddress.From(Factory, Salt, initCode);
    }

    /// <summary>A <c>deploy | self_verify</c> prefix whose sender is the account the factory deploys.</summary>
    private static Transaction DeployTx(Address deployed)
    {
        Transaction tx = FrameTx(nonce: 0, DeployFrame(), SelfVerifyFrame());
        tx.SenderAddress = deployed;
        return tx;
    }

    private static TxFrame ExpiryVerifyFrame()
    {
        byte[] data = new byte[Eip8141Constants.ExpiryDataLength];
        BinaryPrimitives.WriteUInt64BigEndian(data, ulong.MaxValue);
        return new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveScopeNone, Eip8141Constants.ExpiryVerifierAddress,
            gasLimit: 50_000, UInt256.Zero, data);
    }

    // A deploy frame is the one prefix frame that writes state, so it needs a limits.state budget.
    private static TxFrame DeployFrame() =>
        new(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, Factory, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default);

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
