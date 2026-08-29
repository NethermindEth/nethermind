// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-8141 APPROVE authorization scope. A SENDER frame reaches its target with the account as caller
/// without invoking the account's own entrypoint, so what an account authorizes is decided entirely by
/// its authorizing frame. These tests confirm that the authorizing frame can bind APPROVE to data it
/// observes, either account storage or a declared recent-root reference, and that a scope of zero
/// withholds the approval and fails the transaction.
/// </summary>
[TestFixture]
public class FrameTxApproveAuthorizationTests
{
    private static readonly Address Account = TestItem.AddressA;
    private static readonly Address Target = TestItem.AddressC;
    private static readonly Address Beneficiary = TestItem.AddressE;

    private const int ObservedSlot = 0;
    private const int CallerSlot = 0;

    private const ulong ExpectedSlot = 1_000;
    private const ulong OtherSlot = 999;
    private const ulong HeadSlot = 1_001;

    private ISpecProvider _specProvider;
    private IWorldState _state;
    private IDisposable _stateCloser;
    private EthereumTransactionProcessor _processor;
    private IReleaseSpec Spec => _specProvider.GenesisSpec;

    [SetUp]
    public void Setup()
    {
        OverridableReleaseSpec spec = new(Eip8141Prototype.Instance) { IsEip8250Enabled = true, IsEip8272Enabled = true, IsEip7906Enabled = true };
        _specProvider = new TestSpecProvider(spec);
        _state = TestWorldStateFactory.CreateForTest();
        _stateCloser = _state.BeginScope(IWorldState.PreGenesis);
        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _state,
            new EthereumVirtualMachine(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance),
            new EthereumCodeInfoRepository(_state), LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _stateCloser?.Dispose();

    [Test]
    public void SenderFrame_ReachesTarget_WithAccountAsCaller()
    {
        Deploy(Account, StaticApprove(), 1.Ether);
        Deploy(Target, TargetRecordsCaller(), UInt256.Zero);
        SetObservedSlot();

        TransactionResult result = Process(FrameTx(SelfVerify(), SenderFrameTo(Target)));

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(RecordedCaller(), Is.EqualTo(AsWord(Account)));
    }

    [Test]
    public void AuthorizingFrame_WithholdsApprove_WhenObservedStorageIsSet()
    {
        Deploy(Account, StorageGatedApprove(), 1.Ether);
        Deploy(Target, TargetRecordsCaller(), UInt256.Zero);
        SetObservedSlot();

        TransactionResult result = Process(FrameTx(SelfVerify(), SenderFrameTo(Target)));

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
        Assert.That(RecordedCaller(), Is.EqualTo(UInt256.Zero));
    }

    [Test]
    public void AuthorizingFrame_GrantsApprove_WhenObservedStorageIsClear()
    {
        Deploy(Account, StorageGatedApprove(), 1.Ether);
        Deploy(Target, TargetRecordsCaller(), UInt256.Zero);
        _state.Commit(Spec);

        TransactionResult result = Process(FrameTx(SelfVerify(), SenderFrameTo(Target)));

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(RecordedCaller(), Is.EqualTo(AsWord(Account)));
    }

    [Test]
    public void AuthorizingFrame_GrantsApprove_WhenReferenceMatchesExpectedRoot()
    {
        ValueHash256 sourceId = RecentRootStore.SourceId(Account, TestItem.KeccakA.ValueHash256);
        ValueHash256 expected = TestItem.KeccakB.ValueHash256;
        Deploy(Account, ReferenceGatedApprove(expected), 1.Ether);
        Deploy(Target, TargetRecordsCaller(), UInt256.Zero);
        CommitRootEntry(sourceId, ExpectedSlot, expected);

        Transaction tx = FrameTx(SelfVerify(), SenderFrameTo(Target));
        tx.RecentRootReferences = [new RecentRootReference(sourceId, ExpectedSlot, expected)];

        TransactionResult result = ProcessAt(tx, HeadSlot);

        Assert.That(result.TransactionExecuted, Is.True);
        Assert.That(RecordedCaller(), Is.EqualTo(AsWord(Account)));
    }

    [Test]
    public void AuthorizingFrame_WithholdsApprove_WhenReferenceRootDiffers()
    {
        ValueHash256 sourceId = RecentRootStore.SourceId(Account, TestItem.KeccakA.ValueHash256);
        ValueHash256 expected = TestItem.KeccakB.ValueHash256;
        ValueHash256 other = TestItem.KeccakC.ValueHash256;
        Deploy(Account, ReferenceGatedApprove(expected), 1.Ether);
        Deploy(Target, TargetRecordsCaller(), UInt256.Zero);
        CommitRootEntry(sourceId, OtherSlot, other);

        Transaction tx = FrameTx(SelfVerify(), SenderFrameTo(Target));
        tx.RecentRootReferences = [new RecentRootReference(sourceId, OtherSlot, other)];

        TransactionResult result = ProcessAt(tx, HeadSlot);

        Assert.That(result.TransactionExecuted, Is.False);
        Assert.That(result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
        Assert.That(RecordedCaller(), Is.EqualTo(UInt256.Zero));
    }

    private static byte[] StaticApprove() =>
        Prepare.EvmCode
            .PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0)
            .Op(Instruction.APPROVE).Done;

    private static byte[] StorageGatedApprove() =>
        Prepare.EvmCode
            .PushData(ObservedSlot).Op(Instruction.SLOAD).Op(Instruction.ISZERO)
            .PushData(TxFrame.ApproveExecutionAndPayment).Op(Instruction.MUL)
            .PushData(0).PushData(0)
            .Op(Instruction.APPROVE).Done;

    private static byte[] ReferenceGatedApprove(ValueHash256 expectedRoot) =>
        Prepare.EvmCode
            .PushData(0).PushData(2).Op(Instruction.RECENTROOTREFLOAD)
            .PushData(expectedRoot.Bytes.ToArray()).Op(Instruction.EQ)
            .PushData(TxFrame.ApproveExecutionAndPayment).Op(Instruction.MUL)
            .PushData(0).PushData(0)
            .Op(Instruction.APPROVE).Done;

    private static byte[] TargetRecordsCaller() =>
        Prepare.EvmCode
            .Op(Instruction.CALLER).PushData(CallerSlot).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done;

    private void Deploy(Address address, byte[] code, UInt256 balance)
    {
        _state.CreateAccount(address, balance);
        _state.InsertCode(address, code, Spec);
    }

    private void SetObservedSlot()
    {
        _state.Set(new StorageCell(Account, ObservedSlot), [1]);
        _state.Commit(Spec);
    }

    private void CommitRootEntry(ValueHash256 sourceId, ulong slot, ValueHash256 root)
    {
        if (!_state.AccountExists(Eip8272Constants.RecentRootAddress))
            _state.CreateAccount(Eip8272Constants.RecentRootAddress, UInt256.Zero, 1);
        _state.Set(RecentRootStore.ReferenceCell(sourceId, slot),
            RecentRootStore.EntryHash(sourceId, slot, root).Bytes.WithoutLeadingZeros().ToArray());
        _state.Commit(Spec);
    }

    private UInt256 RecordedCaller() => new(_state.Get(new StorageCell(Target, CallerSlot)), isBigEndian: true);

    private static UInt256 AsWord(Address address) => new(address.Bytes, isBigEndian: true);

    private static TxFrame SelfVerify() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default);

    private static TxFrame SenderFrameTo(Address target) =>
        new(TxFrame.ModeSender, 0, target, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, Array.Empty<byte>());

    private static Transaction FrameTx(params TxFrame[] frames) =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Account,
            Frames = frames,
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };

    private TransactionResult Process(Transaction tx)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        return _processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), NullTxTracer.Instance);
    }

    private TransactionResult ProcessAt(Transaction tx, ulong slot)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBeneficiary(Beneficiary)
            .WithTransactions(tx)
            .WithSlotNumber(slot)
            .WithGasLimit(30_000_000).TestObject;
        return _processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), NullTxTracer.Instance);
    }
}
