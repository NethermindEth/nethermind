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
/// EIP-8141 in-pool validation-prefix simulation: the processor's
/// <see cref="ExecutionOptions.FrameValidationPrefixOnly"/> path and <see cref="FrameTxValidationTracer"/>.
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
    private static readonly Address Factory = TestItem.AddressB;
    private static readonly byte[] Salt = new byte[32];

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
        // Banned outside the expiry verifier frame, even though the frame goes on to APPROVE.
        byte[] code = Prepare.EvmCode
            .Op(Instruction.TIMESTAMP).Op(Instruction.POP)
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        DeployContract(Sender, code, 1.Ether);
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        (_, FrameTxValidationTracer tracer) = Simulate(tx);

        Assert.That(tracer.Violated, Is.True);
    }

    [TestCase(Instruction.ORIGIN)]
    [TestCase(Instruction.BLOBHASH)]
    [TestCase(Instruction.TLOAD)]
    public void Simulate_PrefixUsesRelaxedOpcode_ResolvesPayer(Instruction opcode)
    {
        // Each reads the frame or the transaction payload rather than the block environment, so none makes
        // the prefix depend on state that could differ between simulation and inclusion.
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
        // 0xF6 has no Instruction member on any fork we ship, so the EVM's undefined-opcode halt fails the
        // prefix on its own and the tracer needs no rule of its own for it.
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
        Transaction tx = FrameTx(nonce: 0, SelfVerifyFrame());

        (_, FrameTxValidationTracer tracer) = Simulate(tx);

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

        (_, FrameTxValidationTracer tracer) = Simulate(tx);

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

        (_, FrameTxValidationTracer tracer) = Simulate(tx);

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

        (_, FrameTxValidationTracer tracer) = Simulate(tx);

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

    [Test]
    public void Simulate_PrefixDeclaringAnUncommittedRecentRootReference_RejectedBeforeAnyFrameRuns()
    {
        // RECENTROOTREFLOAD reads the envelope on the strength of the pre-state check, so a prefix must
        // never run against references the main path would reject.
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
        // sitting behind any other frame is not the shape the deploy-gap decline describes.
        DeployContract(Sender, ApproveCode(TxFrame.ApproveExecution), 1.Ether);
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, TestItem.AddressC, gasLimit: 200_000, UInt256.Zero, default),
            SelfVerifyFrame());

        (TransactionResult result, _) = Simulate(tx);

        Assert.That(result.ErrorDescription, Does.Not.Contain("deploy frame"));
    }

    [TestCase(0, true, TestName = "a payer covering the EIP-7623 floor resolves")]
    [TestCase(-1, false, TestName = "a payer one wei short of the EIP-7623 floor does not")]
    public void Simulate_PricesTheApproveGateOnTheSameBudgetExecutionEscrows(int balanceDelta, bool resolves)
    {
        // A calldata-heavy prefix prices on the EIP-7623 floor, so the simulated APPROVE gate must use
        // the same budget the main path escrows on.
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
        // A default-mode frame with no approval scope is also the ordinary execution frame, so the
        // deploy-frame decline must not claim this permanently-invalid shape.
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

    private static byte[] CalldataOf(int length)
    {
        byte[] data = new byte[length];
        data.AsSpan().Fill(0xff);
        return data;
    }

    private (TransactionResult, FrameTxValidationTracer) Simulate(Transaction tx, ulong? slotNumber = null)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithTransactions(tx)
            .WithSlotNumber(slotNumber)
            .WithGasLimit(30_000_000).TestObject;
        FrameTxValidationTracer tracer = new(tx.SenderAddress!, Eip8141Constants.ExpiryVerifierAddress, _stateProvider, Spec);
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

    private static TxFrame DeployFrame() =>
        new(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, Factory, gasLimit: 200_000, UInt256.Zero, default);

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
