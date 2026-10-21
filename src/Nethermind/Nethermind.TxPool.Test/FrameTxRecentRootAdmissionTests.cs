// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
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
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>EIP-8272 public-mempool admission of a <c>recent_root_verify</c> frame through the pool's filters and
/// the real validation-prefix simulator, over a head whose <c>slotNumber</c> the test chooses.</summary>
[TestFixture]
public class FrameTxRecentRootAdmissionTests
{
    private const ulong HeadSlot = 10_000;
    private static readonly Address Sender = TestItem.PrivateKeyA.Address;
    private static readonly Address Factory = TestItem.AddressB;
    private static readonly byte[] Salt = new byte[32];
    private static readonly byte[] DeployedCode = Prepare.EvmCode
        .PushData((byte)FrameFlags.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
    private static readonly byte[] DeployInitCode = Prepare.EvmCode.ForInitOf(DeployedCode).Done;
    private static readonly Address Deployed = ContractAddress.From(Factory, Salt, DeployInitCode);
    private static readonly UInt256 SenderBalance = 1_000.Ether;
    private static readonly ValueHash256 SourceId = RecentRootStore.SourceId(TestItem.AddressC, TestItem.KeccakA.ValueHash256);
    private static readonly ValueHash256 Root = TestItem.KeccakB.ValueHash256;

    private readonly ISpecProvider _specProvider = new TestSpecProvider(new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip8272Enabled = true });
    private BasicTestBlockchain? _chain;

    [TearDown]
    public void TearDown() => _chain?.Dispose();

    [TestCase(false, false, TestName = "SubmitTx_CommittedTupleThenSelfVerify_IsAccepted")]
    [TestCase(true, false, TestName = "SubmitTx_ExpiryThenCommittedTupleThenSelfVerify_IsAccepted")]
    [TestCase(false, true, TestName = "SubmitTx_CommittedTupleThenDeployThenSelfVerify_IsAccepted")]
    [TestCase(true, true, TestName = "SubmitTx_ExpiryThenCommittedTupleThenDeployThenSelfVerify_IsAccepted")]
    public async Task SubmitTx_CommittedTupleAheadOfSelfVerification_IsAccepted(bool expiry, bool deploy)
    {
        await BuildHarness(Eip8272Constants.RecentRootCode.ToArray(), committedSlot: HeadSlot);
        List<TxFrame> prefix = [];
        if (expiry) prefix.Add(ExpiryFrame());
        prefix.Add(RecentRootFrame(HeadSlot));
        if (deploy) prefix.Add(DeployFrame());
        prefix.Add(SelfVerifyFrame());

        Assert.That(Submit([.. prefix], deploy ? Deployed : Sender), Is.EqualTo(AcceptTxResult.Accepted));
    }

    [TestCase(0UL, true, TestName = "SubmitTx_TupleWrittenAtTheHeadSlot_IsAccepted")]
    [TestCase(Eip8272Constants.RecentRootUsableWindow - 1, true, TestName = "SubmitTx_TupleAged8190AtTheHead_IsAccepted")]
    [TestCase(Eip8272Constants.RecentRootUsableWindow, false, TestName = "SubmitTx_TupleAged8191AtTheHead_IsRejected")]
    public async Task SubmitTx_TupleAge_IsMeasuredAtTheSlotAfterTheHead(ulong ageAtHead, bool accepted)
    {
        ulong slot = HeadSlot - ageAtHead;
        await BuildHarness(Eip8272Constants.RecentRootCode.ToArray(), committedSlot: slot);

        AcceptTxResult result = Submit([RecentRootFrame(slot), SelfVerifyFrame()]);

        Assert.That(result == AcceptTxResult.Accepted, Is.EqualTo(accepted), result.ToString());
    }

    [Test]
    public async Task SubmitTx_RecentRootAddressWithoutRecentRootCode_IsRejected()
    {
        await BuildHarness(Prepare.EvmCode.Op(Instruction.STOP).Done, committedSlot: HeadSlot);

        Assert.That(Submit([RecentRootFrame(HeadSlot), SelfVerifyFrame()]), Is.EqualTo(AcceptTxResult.FrameSimulationFailed));
    }

    [Test]
    public async Task SubmitTx_SenderReadingRecentRootStorageThroughACall_IsRejected()
    {
        byte[] senderCode = Prepare.EvmCode
            .PushData(Eip8272Constants.RecentRootTupleLength).PushData(0).PushData(0).Op(Instruction.CALLDATACOPY)
            .PushData(0).PushData(0).PushData(Eip8272Constants.RecentRootTupleLength).PushData(0)
            .PushData(Eip8272Constants.RecentRootAddress).Op(Instruction.GAS).Op(Instruction.STATICCALL).Op(Instruction.POP)
            .PushData((byte)FrameFlags.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;
        await BuildHarness(Eip8272Constants.RecentRootCode.ToArray(), committedSlot: HeadSlot, senderCode);
        TxFrame selfVerify = new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, RecentRootFrame(HeadSlot).Data.ToArray());

        Assert.That(Submit([RecentRootFrame(HeadSlot), selfVerify]), Is.EqualTo(AcceptTxResult.FrameSimulationFailed));
    }

    private static TxFrame RecentRootFrame(ulong slot) => FrameTxTestFrames.RecentRootVerify(60_000, (SourceId, slot, Root));

    private static TxFrame SelfVerifyFrame() =>
        new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 60_000, UInt256.Zero, default);

    private static TxFrame ExpiryFrame() => FrameTxTestFrames.ExpiryAt(ulong.MaxValue, gasLimit: 50_000);

    private static TxFrame DeployFrame() =>
        new(FrameMode.Default, FrameFlags.None, Factory, executionGasLimit: 100_000, stateGasLimit: 200_000, UInt256.Zero, default);

    /// <remarks>A <see cref="Deployed"/> sender runs the code its deploy frame installs, which approves without a signature.</remarks>
    private AcceptTxResult Submit(TxFrame[] frames, Address? sender = null)
    {
        sender ??= Sender;
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = _specProvider.ChainId,
            Nonce = 0,
            SenderAddress = sender,
            Frames = frames,
            FrameSignatures = [],
            GasLimit = 1_000_000,
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei,
        };
        if (sender == Sender) FrameTxTestFrames.SignSecp256k1(tx, TestItem.PrivateKeyA, signer: null);
        tx.Hash = tx.CalculateHash();
        return _chain!.TxPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
    }

    /// <summary>The test node's pool and simulator over a genesis head at <see cref="HeadSlot"/>, its state
    /// carrying <paramref name="recentRootCode"/> and one committed entry at <paramref name="committedSlot"/>.</summary>
    private async Task BuildHarness(byte[] recentRootCode, ulong committedSlot, byte[]? senderCode = null) =>
        _chain = await BasicTestBlockchain.Create(builder =>
        {
            builder.AddSingleton(_specProvider);
            builder.WithGenesisPostProcessor((genesis, worldState, specProvider) =>
            {
                genesis.Header.SlotNumber = HeadSlot;
                IReleaseSpec spec = specProvider.GenesisSpec;
                worldState.CreateAccount(Sender, SenderBalance);
                if (senderCode is not null) worldState.InsertCode(Sender, senderCode, spec);
                worldState.CreateAccount(Factory, UInt256.Zero);
                worldState.InsertCode(Factory, Prepare.EvmCode.Create2(DeployInitCode, Salt, 0).Done, spec);
                worldState.CreateAccount(Deployed, SenderBalance);
                worldState.CreateAccountIfNotExists(Eip8141Constants.ExpiryVerifierAddress, UInt256.Zero);
                worldState.InsertCode(Eip8141Constants.ExpiryVerifierAddress, Eip8141Constants.ExpiryVerifierCode, spec);
                worldState.CreateAccount(Eip8272Constants.RecentRootAddress, UInt256.Zero, 1);
                worldState.InsertCode(Eip8272Constants.RecentRootAddress, recentRootCode, spec);
                worldState.Set(RecentRootStore.ReferenceCell(SourceId, committedSlot), RecentRootStore.EntryHash(SourceId, committedSlot, Root).ToUInt256());
                worldState.RecalculateStateRoot();
            });
        });
}
