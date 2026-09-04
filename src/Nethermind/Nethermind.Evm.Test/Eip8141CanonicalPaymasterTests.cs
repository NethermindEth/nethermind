// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-8141 canonical paymaster: the assembled reference runtime bytecode exercised end-to-end
/// under the prototype fork. The bytecode is produced by CanonicalPaymaster/paymaster_asm.py
/// (two-pass label resolution); <see cref="PaymasterRuntimeHex"/> and <see cref="CanonicalCodeHash"/>
/// must stay in sync with that script — the code hash is the mempool recognition anchor.
/// </summary>
[TestFixture]
public class Eip8141CanonicalPaymasterTests
{
    // Output of CanonicalPaymaster/paymaster_asm.py.
    private const string PaymasterRuntimeHex =
        "0x3461002e57366100355760016001b41561005a575f6001b45f54141561005a5760026001b461005a5760015f5faa5b3661005a" +
        "57005b5f3560f81c8060011461005e57806002146100a557806003146100ec57600414610123575b5f5ffd5b50335f5414610087" +
        "5760016001b41561005a575f6001b45f54141561005a5760026001b461005a575b60025461005a57600135801561005a57600155" +
        "426201518001600255005b50335f54146100ce5760016001b41561005a575f6001b45f54141561005a5760026001b461005a575b" +
        "60025461005a57600135801561005a57600355426201518001600255005b50335f54146101155760016001b41561005a575f6001" +
        "b45f54141561005a5760026001b461005a575b5f6001555f6002555f600355005b600254801561005a57421061005a5760015480" +
        "15610153575f6001555f6002555f5f5f5f845f545af11561005a57005b506003545f555f6003555f60025500";

    private const string CanonicalCodeHash = "0xda42f0d11838c4c0c3129b8b8e93e9718127ad6b315e517e1088125707c4d45c";

    private const ulong BlockTimestamp = 1_000_000;
    private const ulong Delay = 86_400;

    // Below MaxCost for the sponsored tx (intrinsic + the sum of the frame gas limits, at 1 wei/gas),
    // so an instance deployed with this balance cannot back the sponsorship until a deposit tops it up.
    private static readonly UInt256 UnderfundedBalance = 1_000;

    // Measured pay-frame validation gas: 110 gas of opcodes, one cold SLOAD (ColdStorageAccess, 2,100)
    // and the frame's entry access on the cold paymaster (ColdAccountAccess, 3,000) — the entry access is
    // what puts it above the design doc §5a ~2,150 estimate. Still far under the 15,000 pay-frame bound.
    private const ulong ValidationPathGas = 5_210;

    private static readonly byte[] PaymasterCode = Bytes.FromHexString(PaymasterRuntimeHex);

    // Middle byte of the DELAY constant (PUSH3 0x015180) in the withdrawal-initiate path, located at
    // runtime so it tracks the bytecode — a mutation here changes the code hash without touching the
    // validation path.
    private static readonly int NearMatchMutationOffset =
        PaymasterCode.AsSpan().IndexOf<byte>([0x62, 0x01, 0x51, 0x80]) + 2;

    private static readonly Address Sender = TestItem.AddressA;
    private static readonly PrivateKey SenderKey = TestItem.PrivateKeyA;
    private static readonly Address Signer = TestItem.PrivateKeyB.Address;
    private static readonly PrivateKey SignerKey = TestItem.PrivateKeyB;
    private static readonly Address Recipient = Address.FromNumber(0x1141);
    private static readonly Address Paymaster = TestItem.AddressD;
    private static readonly Address ThirdParty = TestItem.PrivateKeyE.Address;
    private static readonly PrivateKey ThirdPartyKey = TestItem.PrivateKeyE;
    private static readonly Address NonSigner = TestItem.PrivateKeyC.Address;
    private static readonly PrivateKey NonSignerKey = TestItem.PrivateKeyC;
    private static readonly Address NewSigner = TestItem.AddressF;
    private static readonly Address RevertingReceiver = Address.FromNumber(0x7141);
    private static readonly Address ReentrantSigner = Address.FromNumber(0x8141);

    // A contract that unconditionally reverts on any call (PUSH0 PUSH0 REVERT), used as a
    // withdrawal target that rejects the incoming value.
    private static readonly byte[] RevertOnCallCode = [0x5f, 0x5f, 0xfd];

    private static readonly TxFrame OnlyVerifyFrame =
        new(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 100_000, UInt256.Zero, default);
    private static readonly TxFrame PayFrame =
        new(TxFrame.ModeVerify, TxFrame.ApprovePayment, Paymaster, gasLimit: 15_000, UInt256.Zero, default);
    private static readonly TxFrame ActionFrame =
        new(TxFrame.ModeSender, 0, Recipient, gasLimit: 200_000, UInt256.Zero, default);

    private readonly Ecdsa _ecdsa = new();
    private ISpecProvider _specProvider;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private IDisposable _worldStateCloser;
    private IReleaseSpec Spec => _specProvider.GenesisSpec;

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

    // 1. A sponsored codeless EOA pays through the real bytecode: [only_verify, pay, action] with the
    // sponsor's SECP256K1 authorization at signature index 1. The paymaster instance is charged and
    // reported as the payer; the sender pays nothing.
    [Test]
    public void SponsoredEoa_E2E_ChargesPaymasterAndSetsPayer()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);
        Transaction tx = SponsoredTx(0);
        SignEntries(tx, [(SenderKey, null, []), (SignerKey, Signer, [])]);

        TxReceipt receipt = ProcessBlock(BlockTimestamp, tx)[0];

        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(receipt.Payer, Is.EqualTo(Paymaster), "the canonical instance is the payer");
        Assert.That(_stateProvider.GetBalance(Paymaster), Is.EqualTo(1.Ether - (UInt256)receipt.GasUsed), "gas is charged to the instance");
        Assert.That(_stateProvider.GetBalance(Sender), Is.EqualTo(1.Ether), "the sponsored sender pays nothing");
        Assert.That(_stateProvider.GetNonce(Sender), Is.EqualTo(1UL), "payment approval consumes the sender nonce");
    }

    // 2. A valid signature at index 1 whose signer is not the stored signer: the pay frame's identity
    // check reverts, and a reverting VERIFY frame invalidates the whole transaction.
    [Test]
    public void WrongSignerAtIndex1_PayFrameReverts_TxInvalid()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);
        Transaction tx = SponsoredTx(0);
        SignEntries(tx, [(SenderKey, null, []), (NonSignerKey, NonSigner, [])]);

        TransactionResult result = ExecuteInvalid(tx, BlockTimestamp);

        Assert.That(result.TransactionExecuted, Is.False);
    }

    // 3. No signature entry at index 1: SIGPARAM(scheme, 1) is out of bounds and exceptionally halts
    // the pay frame, invalidating the transaction — zero contract code handles the missing entry.
    [Test]
    public void MissingIndex1Entry_ExceptionalHalt_TxInvalid()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);
        Transaction tx = SponsoredTx(0);
        SignEntries(tx, [(SenderKey, null, [])]);

        TransactionResult result = ExecuteInvalid(tx, BlockTimestamp);

        Assert.That(result.TransactionExecuted, Is.False);
    }

    // 4. A bespoke digest signature (non-empty msg) at index 1 passes protocol pre-flight but the
    // paymaster requires the canonical sig hash (empty msg), so it reverts — a quote-style signature
    // cannot stand in for authorization over the whole transaction.
    [Test]
    public void NonEmptyMsgAtIndex1_PayFrameReverts_TxInvalid()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);
        ValueHash256 bespoke = Keccak.Compute("bespoke authorization");
        Transaction tx = SponsoredTx(0);
        SignEntries(tx, [(SenderKey, null, []), (SignerKey, Signer, bespoke.ToByteArray())]);

        TransactionResult result = ExecuteInvalid(tx, BlockTimestamp);

        Assert.That(result.TransactionExecuted, Is.False);
    }

    // 5. Deposits: a plain value transfer with empty calldata is accepted; value with calldata reverts
    // because administrative operations are non-payable.
    [Test]
    public void Deposit_PlainValueAccepted_ValueWithDataReverts()
    {
        DeployPaymaster(PaymasterCode, Signer, UInt256.Zero);
        Fund(ThirdParty);

        TxReceipt accepted = ProcessBlock(BlockTimestamp, AdminTx(ThirdPartyKey, 0, [], value: 1_000))[0];
        Assert.That(accepted.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(_stateProvider.GetBalance(Paymaster), Is.EqualTo((UInt256)1_000), "the plain deposit is credited");

        TxReceipt rejected = ProcessBlock(BlockTimestamp, AdminTx(ThirdPartyKey, 1, [0xff], value: 1_000))[0];
        Assert.That(rejected.StatusCode, Is.EqualTo(StatusCode.Failure));
        Assert.That(_stateProvider.GetBalance(Paymaster), Is.EqualTo((UInt256)1_000), "the rejected deposit moves no value");
    }

    // 6. Withdrawal lifecycle: the signer initiates, a premature finalize reverts and leaves the
    // pending state intact, and a matured finalize pays the signer and clears the slots.
    [Test]
    public void WithdrawalLifecycle_InitiatePrematureFinalizeMaturedPaysSigner()
    {
        Fund(Signer);
        Fund(ThirdParty);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);

        UInt256 amount = 500;
        ProcessBlock(BlockTimestamp, AdminTx(SignerKey, 0, WithdrawalCall(amount)));
        Assert.That(Slot(1), Is.EqualTo(amount), "slot 1 records the pending amount");
        Assert.That(Slot(2), Is.EqualTo((UInt256)(BlockTimestamp + Delay)), "slot 2 is the maturity time");

        TxReceipt premature = ProcessBlock(BlockTimestamp, AdminTx(ThirdPartyKey, 0, FinalizeCall()))[0];
        Assert.That(premature.StatusCode, Is.EqualTo(StatusCode.Failure), "finalizing before maturity reverts");
        Assert.That(Slot(1), Is.EqualTo(amount), "the pending withdrawal survives a premature finalize");

        UInt256 signerBefore = _stateProvider.GetBalance(Signer);
        UInt256 paymasterBefore = _stateProvider.GetBalance(Paymaster);
        TxReceipt matured = ProcessBlock(BlockTimestamp + Delay, AdminTx(ThirdPartyKey, 1, FinalizeCall()))[0];
        Assert.That(matured.StatusCode, Is.EqualTo(StatusCode.Success), "a matured withdrawal finalizes");
        Assert.That(_stateProvider.GetBalance(Signer) - signerBefore, Is.EqualTo(amount), "the amount is paid to the signer");
        Assert.That(paymasterBefore - _stateProvider.GetBalance(Paymaster), Is.EqualTo(amount));
        Assert.That(Slot(1), Is.EqualTo(UInt256.Zero));
        Assert.That(Slot(2), Is.EqualTo(UInt256.Zero));
    }

    // 7. Rotation lifecycle: the signer initiates a rotation, a matured finalize writes the new signer
    // into slot 0, and an authorization by the previous signer then fails validation.
    [Test]
    public void RotationLifecycle_FinalizeRotatesSignerAndInvalidatesOldSigner()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        Fund(Signer);
        Fund(ThirdParty);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);

        ProcessBlock(BlockTimestamp, AdminTx(SignerKey, 0, RotationCall(NewSigner)));
        Assert.That(Slot(3), Is.EqualTo(new UInt256(NewSigner.Bytes, isBigEndian: true)), "slot 3 holds the pending signer");

        TxReceipt matured = ProcessBlock(BlockTimestamp + Delay, AdminTx(ThirdPartyKey, 0, FinalizeCall()))[0];
        Assert.That(matured.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(Slot(0), Is.EqualTo(new UInt256(NewSigner.Bytes, isBigEndian: true)), "the rotation writes slot 0");
        Assert.That(Slot(3), Is.EqualTo(UInt256.Zero));
        Assert.That(Slot(2), Is.EqualTo(UInt256.Zero));

        Transaction tx = SponsoredTx(0);
        SignEntries(tx, [(SenderKey, null, []), (SignerKey, Signer, [])]);
        TransactionResult result = ExecuteInvalid(tx, BlockTimestamp + Delay);
        Assert.That(result.TransactionExecuted, Is.False, "the outgoing signer's authorization is invalid after rotation");
    }

    // 8. Cancel clears the pending withdrawal/rotation slots (1-3) and leaves the signer intact.
    [Test]
    public void Cancel_ClearsPendingSlots()
    {
        Fund(Signer);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);

        ProcessBlock(BlockTimestamp, AdminTx(SignerKey, 0, WithdrawalCall(500)));
        Assert.That(Slot(2), Is.Not.EqualTo(UInt256.Zero));

        TxReceipt cancel = ProcessBlock(BlockTimestamp, AdminTx(SignerKey, 1, CancelCall()))[0];
        Assert.That(cancel.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(Slot(1), Is.EqualTo(UInt256.Zero));
        Assert.That(Slot(2), Is.EqualTo(UInt256.Zero));
        Assert.That(Slot(3), Is.EqualTo(UInt256.Zero));
        Assert.That(Slot(0), Is.EqualTo(new UInt256(Signer.Bytes, isBigEndian: true)), "the signer is untouched by cancel");
    }

    // 9. A non-signer cannot initiate, and the signer cannot open a second action while one is pending.
    [Test]
    public void AdminInitiate_RejectsNonSignerAndPendingAction()
    {
        Fund(Signer);
        Fund(NonSigner);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);

        TxReceipt fromNonSigner = ProcessBlock(BlockTimestamp, AdminTx(NonSignerKey, 0, WithdrawalCall(500)))[0];
        Assert.That(fromNonSigner.StatusCode, Is.EqualTo(StatusCode.Failure), "a non-signer cannot initiate");
        Assert.That(Slot(2), Is.EqualTo(UInt256.Zero), "a rejected initiate creates no pending action");

        ProcessBlock(BlockTimestamp, AdminTx(SignerKey, 0, WithdrawalCall(500)));
        TxReceipt whilePending = ProcessBlock(BlockTimestamp, AdminTx(SignerKey, 1, RotationCall(NewSigner)))[0];
        Assert.That(whilePending.StatusCode, Is.EqualTo(StatusCode.Failure), "a second action while one is pending is rejected");
        Assert.That(Slot(3), Is.EqualTo(UInt256.Zero), "the blocked rotation writes nothing");
        Assert.That(Slot(1), Is.EqualTo((UInt256)500), "the pending withdrawal is untouched");
    }

    // 10a. The mempool recognition anchor: the assembled reference bytecode hashes to the pinned
    // canonical code hash. This is the single assertion that fails if the assembler output ever drifts.
    [Test]
    public void AssembledBytecode_MatchesCanonicalCodeHash() =>
        Assert.That(Keccak.Compute(PaymasterCode), Is.EqualTo(new Hash256(CanonicalCodeHash)),
            "the assembled bytecode must hash to the pinned canonical code hash");

    // 10b. A one-opcode mutation keeps the validation path behaving, but its code hash no longer matches
    // the pinned canonical hash — the mempool recognition fixture: recognition fails closed, not behavior.
    [Test]
    public void NearMatchBytecode_BehavesButCodeHashDiffers()
    {
        byte[] nearMatch = (byte[])PaymasterCode.Clone();
        nearMatch[NearMatchMutationOffset] ^= 0x01;
        Assert.That(Keccak.Compute(nearMatch), Is.Not.EqualTo(new Hash256(CanonicalCodeHash)),
            "a one-opcode mutation must fail exact-hash recognition");

        _stateProvider.CreateAccount(Sender, 1.Ether);
        DeployPaymaster(nearMatch, Signer, 1.Ether);
        Transaction tx = SponsoredTx(0);
        SignEntries(tx, [(SenderKey, null, []), (SignerKey, Signer, [])]);

        TxReceipt receipt = ProcessBlock(BlockTimestamp, tx)[0];
        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), "the near match still validates on the hot path");
        Assert.That(receipt.Payer, Is.EqualTo(Paymaster));
    }

    // 11. The pay-frame validation path is the frame-entry account access, a cold SLOAD, three SIGPARAM
    // reads, dispatch and APPROVE; it sits far under the recommended 15,000 gas pay-frame bound.
    [Test]
    public void ValidationPathGas_MatchesBudget()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);
        Transaction tx = SponsoredTx(0);
        SignEntries(tx, [(SenderKey, null, []), (SignerKey, Signer, [])]);

        TxReceipt receipt = ProcessBlock(BlockTimestamp, tx)[0];
        ulong validationGas = receipt.FrameReceipts![1].GasUsed;

        Assert.That(validationGas, Is.EqualTo(ValidationPathGas), "the pinned validation-path gas");
        Assert.That(validationGas, Is.LessThan(15_000UL), "well under the recommended pay-frame gas bound");
    }

    // 12. Audit finding F1 (corrected). F1 was derived from a stale ethereum/EIPs#12012 draft; the
    // live spec reserves the 32-byte all-zero digest as an invalid msg, so the value check and the
    // empty-msg requirement coincide exactly and there is no live footgun. Nethermind already enforces
    // this in static validation (FrameTxValidation.ZeroDigestMsg), so a full node never admits such a
    // tx. This test pins both layers:
    //   (a) the real, end-to-end defense — static validation rejects the sponsored tx before it can
    //       execute; and
    //   (b) the processor enforces the same constraints, so the paymaster's own SIGPARAM(msg, 1) != 0
    //       gate — a value check, which a 32-byte zero digest would pass — is unreachable either way.
    [Test]
    public void AllZeroMsgAtIndex1_RejectedByBothValidationLayers()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);
        Transaction tx = SponsoredTx(0);
        SignEntries(tx, [(SenderKey, null, []), (SignerKey, Signer, new byte[32])]);

        // (a) A full node rejects the tx as malformed before any frame runs.
        bool wellFormed = FrameTxValidation.IsWellFormed(tx, Spec.IsEip7906Enabled, out string? error);
        Assert.That(wellFormed, Is.False, "static validation rejects a 32-byte zero digest msg");
        Assert.That(error, Is.EqualTo(FrameTxValidation.ZeroDigestMsg));

        // (b) The processor enforces the same structural constraints, so driving it directly no longer
        // reaches the paymaster: the transaction produces no receipt at all.
        Assert.That(ProcessBlock(BlockTimestamp, tx), Is.Empty, "the processor declines it before any frame runs");
    }

    // 13/14. A matured withdrawal whose value-bearing CALL to the signer fails reverts the whole
    // finalize and preserves the pending slots, so finalize can be retried. Two failure modes: the
    // pending amount exceeds the paymaster balance, and the signer is a contract that reverts on
    // receiving the value.
    [TestCaseSource(nameof(FinalizeRevertCases))]
    public void MaturedFinalize_FailedWithdrawalCall_RevertsAndPreservesPending(bool signerRejectsValue, UInt256 amount)
    {
        Fund(ThirdParty);
        Address signer = signerRejectsValue ? RevertingReceiver : Signer;
        if (signerRejectsValue) DeployContract(RevertingReceiver, RevertOnCallCode, UInt256.Zero);
        DeployPaymaster(PaymasterCode, signer, 1.Ether);
        SetPending(amount, BlockTimestamp);

        AssertFinalizeRevertsAndPreservesPending(BlockTimestamp, amount);
    }

    private static IEnumerable<TestCaseData> FinalizeRevertCases()
    {
        yield return new TestCaseData(false, 2.Ether)
            .SetName("the pending amount exceeds the paymaster balance");
        yield return new TestCaseData(true, (UInt256)100)
            .SetName("the signer contract reverts on receiving the value");
    }

    // 15. Reentrancy during the withdrawal CALL is safe: the signer is a contract that re-enters the
    // paymaster while receiving the value. Checks-effects clears slots 1 and 2 before the CALL, so a
    // re-entrant finalize hits the cleared timelock and reverts (no double spend), and a re-entrant
    // initiate — authorized because CALLER equals slot 0 — only opens a fresh timelocked pending (no
    // drain). Either way the signer is paid exactly once.
    [TestCaseSource(nameof(ReentrancyCases))]
    public void FinalizeReentrancy_NoDoubleSpendOrDrain(byte[] reentryCalldata, UInt256 expectedSlot1, UInt256 expectedSlot2)
    {
        Fund(ThirdParty);
        DeployContract(ReentrantSigner, BuildReentrantCaller(reentryCalldata), UInt256.Zero);
        DeployPaymaster(PaymasterCode, ReentrantSigner, 1.Ether);
        UInt256 amount = 500;
        SetPending(amount, BlockTimestamp);
        UInt256 paymasterBefore = _stateProvider.GetBalance(Paymaster);

        TxReceipt receipt = ProcessBlock(BlockTimestamp, AdminTx(ThirdPartyKey, 0, FinalizeCall()))[0];

        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), "the outer withdrawal completes once");
        Assert.That(_stateProvider.GetBalance(ReentrantSigner), Is.EqualTo(amount), "the signer is paid exactly once");
        Assert.That(paymasterBefore - _stateProvider.GetBalance(Paymaster), Is.EqualTo(amount), "the paymaster is drained by one withdrawal only");
        Assert.That(Slot(1), Is.EqualTo(expectedSlot1));
        Assert.That(Slot(2), Is.EqualTo(expectedSlot2));
    }

    private static IEnumerable<TestCaseData> ReentrancyCases()
    {
        yield return new TestCaseData(FinalizeCall(), UInt256.Zero, UInt256.Zero)
            .SetName("re-entrant finalize hits the cleared timelock and leaves no pending");
        yield return new TestCaseData(WithdrawalCall(200), (UInt256)200, (UInt256)(BlockTimestamp + Delay))
            .SetName("re-entrant initiate opens a fresh timelocked pending without draining");
    }

    // 16. Admin authorization through the frame-tx signature route (authorized() = signer-signed entry
    // at index 1: scheme != ARBITRARY, resolved_signer == slot 0, empty msg). The sender self-sponsors
    // via index 0, leaving index 1 free to carry the admin authorization, and a SENDER frame targeting
    // the paymaster with initiate-withdrawal calldata is authorized purely by the signature.
    [Test]
    public void AdminViaSignatureRoute_SignerAtIndex1_InitiatesWithdrawal()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);
        UInt256 amount = 500;
        Transaction tx = AdminFrameTx(0, amount);
        SignEntries(tx, [(SenderKey, null, []), (SignerKey, Signer, [])]);

        TxReceipt receipt = ProcessBlock(BlockTimestamp, tx)[0];

        Assert.That(receipt.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusSuccess), "the signature route authorizes the admin op");
        Assert.That(Slot(1), Is.EqualTo(amount), "the withdrawal is opened");
        Assert.That(Slot(2), Is.EqualTo((UInt256)(BlockTimestamp + Delay)));
    }

    // 17. The signature route does not authorize when the index-1 entry is a non-signer, an ARBITRARY
    // scheme, an entry at the wrong index (signer at index 2, not 1), or a signer over a non-empty msg.
    // The admin frame reverts and opens no withdrawal.
    [TestCase(AdminSigCase.NonSigner)]
    [TestCase(AdminSigCase.Arbitrary)]
    [TestCase(AdminSigCase.WrongIndex)]
    [TestCase(AdminSigCase.NonEmptyMsg)]
    public void AdminViaSignatureRoute_RejectsUnauthorizedIndex1(AdminSigCase sigCase)
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);
        Transaction tx = AdminFrameTx(0, 500);
        switch (sigCase)
        {
            case AdminSigCase.NonSigner:
                SignEntries(tx, [(SenderKey, null, []), (NonSignerKey, NonSigner, [])]);
                break;
            case AdminSigCase.WrongIndex:
                SignEntries(tx, [(SenderKey, null, []), (NonSignerKey, NonSigner, []), (SignerKey, Signer, [])]);
                break;
            case AdminSigCase.NonEmptyMsg:
                ValueHash256 bespoke = Keccak.Compute("bespoke");
                SignEntries(tx, [(SenderKey, null, []), (SignerKey, Signer, bespoke.ToByteArray())]);
                break;
            case AdminSigCase.Arbitrary:
                SignSenderWithArbitraryAtIndex1(tx);
                break;
        }

        TxReceipt receipt = ProcessBlock(BlockTimestamp, tx)[0];

        Assert.That(receipt.FrameReceipts![1].Status, Is.EqualTo(TxFrameReceipt.StatusFailure), "an unauthorized signature reverts the admin frame");
        Assert.That(Slot(1), Is.EqualTo(UInt256.Zero), "no withdrawal is opened");
        Assert.That(Slot(2), Is.EqualTo(UInt256.Zero));
    }

    // 18. finalize (op byte 0x04) carrying value hits the value check first (CALLVALUE), routing to the
    // non-payable deposit guard, which reverts on any calldata — the value + calldata combination is
    // rejected before the op byte is ever dispatched, and no value moves.
    [Test]
    public void Finalize_WithValue_RoutesToDepositGuardAndReverts()
    {
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);
        Fund(ThirdParty);
        UInt256 paymasterBefore = _stateProvider.GetBalance(Paymaster);

        TxReceipt receipt = ProcessBlock(BlockTimestamp, AdminTx(ThirdPartyKey, 0, FinalizeCall(), value: 1_000))[0];

        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Failure), "value with calldata is non-payable and reverts");
        Assert.That(_stateProvider.GetBalance(Paymaster), Is.EqualTo(paymasterBefore), "no value is transferred");
    }

    // 19. The pay frame's solvency gate backs the sponsorship with the paymaster's own balance:
    // APPROVE reverts when the instance holds less than MaxCost (EvmInstructions.FrameTx.cs), so an
    // underfunded instance cannot sponsor and the reverting pay frame invalidates the whole tx.
    [Test]
    public void UnderfundedPaymaster_PayFrameReverts_TxInvalid()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        DeployPaymaster(PaymasterCode, Signer, UnderfundedBalance);
        Transaction tx = SponsoredTx(0);
        SignEntries(tx, [(SenderKey, null, []), (SignerKey, Signer, [])]);

        TransactionResult result = ExecuteInvalid(tx, BlockTimestamp);

        Assert.That(result.TransactionExecuted, Is.False);
        // Pin the specific gate: APPROVE's balance check reverts the pay (VERIFY) frame, giving
        // "VERIFY frame reverted". The charge-time gate (TransactionProcessorBase.FrameTx.cs) would
        // instead leave the payer unset and fail with "frame transaction never set a payer", so this
        // assertion distinguishes the two solvency gates rather than accepting either.
        Assert.That(result.ErrorDescription, Is.EqualTo("VERIFY frame reverted"));
    }

    // 20. The mirror of test 19, giving the deposit path (test 5) an end-to-end purpose: a plain-value
    // deposit lifts the same underfunded instance above MaxCost, so the identical sponsored tx now
    // clears the solvency gate, validates, and is charged as the payer.
    [Test]
    public void DepositLiftsPaymasterAboveMaxCost_SponsoredTxBecomesValid()
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        DeployPaymaster(PaymasterCode, Signer, UnderfundedBalance);
        Fund(ThirdParty);

        // 1,000,000 wei clears MaxCost (intrinsic + the sum of the frame gas limits, at 1 wei/gas) with ample margin.
        ProcessBlock(BlockTimestamp, AdminTx(ThirdPartyKey, 0, [], value: 1_000_000));
        Assert.That(_stateProvider.GetBalance(Paymaster), Is.EqualTo(UnderfundedBalance + 1_000_000), "the deposit is credited");

        Transaction tx = SponsoredTx(0);
        SignEntries(tx, [(SenderKey, null, []), (SignerKey, Signer, [])]);
        TxReceipt receipt = ProcessBlock(BlockTimestamp, tx)[0];

        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success), "the funded instance clears the solvency gate");
        Assert.That(receipt.Payer, Is.EqualTo(Paymaster), "the deposit-backed instance is the payer");
    }

    // 21. The initiate guards reject a zero argument: an initiate-withdrawal of 0 (its DUP1 ISZERO
    // JUMPI FAIL) and an initiate-rotation to address(0) (its mirror guard) both revert, so a rotation
    // that would brick the instance never opens a pending action. An unrecognised op byte falls through
    // the dispatch to the shared FAIL and reverts likewise. Either way the pending slots stay clear.
    [TestCaseSource(nameof(InitiateGuardRejectionCases))]
    public void AdminInitiate_RejectsZeroArgumentAndUnknownOp(byte[] call)
    {
        Fund(Signer);
        DeployPaymaster(PaymasterCode, Signer, 1.Ether);

        TxReceipt receipt = ProcessBlock(BlockTimestamp, AdminTx(SignerKey, 0, call))[0];

        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Failure), "the guard reverts");
        Assert.That(Slot(1), Is.EqualTo(UInt256.Zero), "no pending amount is written");
        Assert.That(Slot(2), Is.EqualTo(UInt256.Zero), "no pending action is opened");
        Assert.That(Slot(3), Is.EqualTo(UInt256.Zero), "no pending signer is written");
    }

    private static IEnumerable<TestCaseData> InitiateGuardRejectionCases()
    {
        yield return new TestCaseData(WithdrawalCall(UInt256.Zero))
            .SetName("initiate withdrawal of zero amount reverts");
        yield return new TestCaseData(RotationCall(Address.Zero))
            .SetName("initiate rotation to address(0) reverts");
        yield return new TestCaseData(new byte[] { 0x05 })
            .SetName("an unrecognised op byte reverts");
    }

    public enum AdminSigCase
    {
        NonSigner,
        Arbitrary,
        WrongIndex,
        NonEmptyMsg,
    }

    private static Transaction SponsoredTx(ulong nonce) =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = nonce,
            SenderAddress = Sender,
            Frames = [OnlyVerifyFrame, PayFrame, ActionFrame],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };

    private void SignEntries(Transaction tx, (PrivateKey Key, Address? Signer, byte[] Msg)[] specs)
    {
        // Explicit-digest (non-empty msg) entries sign their own digest independently of the sig hash,
        // and their raw signature bytes are part of the sig-hash preimage (only empty-msg entries are
        // elided). So finalize those entries before computing the hash; the empty-msg entries, which
        // sign the canonical sig hash and are elided from it, are filled in afterwards.
        TxFrameSignature[] signed = new TxFrameSignature[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            ReadOnlyMemory<byte> signatureBytes = default;
            if (specs[i].Msg.Length != 0)
            {
                ValueHash256 explicitDigest = new(specs[i].Msg);
                signatureBytes = ToVrs(_ecdsa.Sign(specs[i].Key, in explicitDigest));
            }
            signed[i] = new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, specs[i].Signer, specs[i].Msg, signatureBytes);
        }
        tx.FrameSignatures = signed;

        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        for (int i = 0; i < specs.Length; i++)
        {
            if (specs[i].Msg.Length == 0)
            {
                signed[i] = new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, specs[i].Signer, specs[i].Msg, ToVrs(_ecdsa.Sign(specs[i].Key, in sigHash)));
            }
        }
        tx.FrameSignatures = signed;
    }

    private static byte[] ToVrs(Signature signature)
    {
        byte[] bytes = new byte[TxFrameSignature.Secp256k1SignatureLength];
        bytes[0] = signature.RecoveryId;
        signature.Bytes.CopyTo(bytes.AsSpan(1));
        return bytes;
    }

    private Transaction AdminTx(PrivateKey key, ulong nonce, byte[] data, UInt256 value = default) =>
        Build.A.Transaction
            .WithTo(Paymaster)
            .WithData(data)
            .WithValue(value)
            .WithNonce(nonce)
            .WithGasLimit(1_000_000)
            .WithGasPrice(1)
            .SignedAndResolved(key)
            .TestObject;

    private static byte[] WithdrawalCall(UInt256 amount) => AdminCall(0x01, amount);

    private static byte[] RotationCall(Address newSigner) => AdminCall(0x02, new UInt256(newSigner.Bytes, isBigEndian: true));

    private static byte[] CancelCall() => [0x03];

    private static byte[] FinalizeCall() => [0x04];

    private static byte[] AdminCall(byte op, UInt256 argument)
    {
        byte[] data = new byte[33];
        data[0] = op;
        argument.ToBigEndian(data.AsSpan(1));
        return data;
    }

    private void Fund(Address address)
    {
        _stateProvider.CreateAccount(address, 1.Ether);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
    }

    private void DeployPaymaster(byte[] code, Address signer, UInt256 balance)
    {
        _stateProvider.CreateAccount(Paymaster, balance);
        _stateProvider.InsertCode(Paymaster, code, Spec);
        _stateProvider.Set(new StorageCell(Paymaster, UInt256.Zero), signer.Bytes.WithoutLeadingZeros().ToArray());
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
    }

    private UInt256 Slot(int slot) =>
        new(_stateProvider.Get(new StorageCell(Paymaster, (UInt256)slot)), isBigEndian: true);

    private Transaction AdminFrameTx(ulong nonce, UInt256 amount)
    {
        // Frame 0: the sender self-sponsors (execution + payment) via signature index 0, freeing
        // index 1 for the admin authorization. Frame 1: a SENDER frame calls the paymaster with
        // initiate-withdrawal calldata, reaching the admin path.
        TxFrame sponsorFrame =
            new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default);
        // The admin op writes the pending amount and its deadline, so the frame needs a limits.state
        // budget; the execution-only constructor would leave it at zero and fail before the auth check.
        TxFrame adminFrame =
            new(TxFrame.ModeSender, 0, Paymaster, executionGasLimit: 1_000_000, stateGasLimit: 200_000, UInt256.Zero, WithdrawalCall(amount));
        return new Transaction
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = nonce,
            SenderAddress = Sender,
            Frames = [sponsorFrame, adminFrame],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };
    }

    private void SignSenderWithArbitraryAtIndex1(Transaction tx)
    {
        // Index 0: the sender's canonical-hash SECP256K1 self-sponsor. Index 1: an ARBITRARY entry,
        // which the paymaster rejects because SIGPARAM(scheme, 1) is zero. ARBITRARY signature bytes
        // are elided from the sig hash, so the digest depends only on index 0's scheme/signer/msg.
        TxFrameSignature arbitrary = new(TxFrameSignature.SchemeArbitrary, null, default, new byte[] { 0x01 });
        tx.FrameSignatures =
        [
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, default),
            arbitrary,
        ];
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        Signature senderSignature = _ecdsa.Sign(SenderKey, in sigHash);
        tx.FrameSignatures =
        [
            new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, ToVrs(senderSignature)),
            arbitrary,
        ];
    }

    private void DeployContract(Address address, byte[] code, UInt256 balance)
    {
        _stateProvider.CreateAccount(address, balance);
        _stateProvider.InsertCode(address, code, Spec);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
    }

    private void SetPending(UInt256 amount, ulong unlockTime)
    {
        StoreSlot(1, amount);
        StoreSlot(2, unlockTime);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
    }

    private void StoreSlot(int slot, UInt256 value) =>
        _stateProvider.Set(new StorageCell(Paymaster, (UInt256)slot), value.ToBigEndian().WithoutLeadingZeros().ToArray());

    // Finalizes exactly at maturity (the pending was set to unlock at the same timestamp), so the
    // revert is driven by the failing withdrawal CALL rather than the timelock.
    private void AssertFinalizeRevertsAndPreservesPending(ulong timestamp, UInt256 amount)
    {
        TxReceipt receipt = ProcessBlock(timestamp, AdminTx(ThirdPartyKey, 0, FinalizeCall()))[0];
        Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Failure), "a failed withdrawal CALL reverts the finalize");
        Assert.That(Slot(1), Is.EqualTo(amount), "the pending amount survives so finalize can be retried");
        Assert.That(Slot(2), Is.EqualTo((UInt256)timestamp), "the maturity time survives the revert");
    }

    private static byte[] BuildReentrantCaller(byte[] payload)
    {
        // Runtime that copies the appended payload into memory and re-enters the paymaster with it,
        // discarding the inner call result (POP). Models a malicious signer re-entering during the
        // withdrawal CALL. The prelude is a fixed 37 bytes (single-byte pushes plus one PUSH20), so
        // the payload is appended at offset 37.
        const byte payloadOffset = 37;
        byte length = (byte)payload.Length;
        List<byte> code =
        [
            0x60, length,          // PUSH1 length
            0x60, payloadOffset,   // PUSH1 payloadOffset
            0x5f,                  // PUSH0 destOffset
            0x39,                  // CODECOPY
            0x5f,                  // PUSH0 retLength
            0x5f,                  // PUSH0 retOffset
            0x60, length,          // PUSH1 argsLength
            0x5f,                  // PUSH0 argsOffset
            0x5f,                  // PUSH0 value
            0x73,                  // PUSH20 paymaster
        ];
        code.AddRange(Paymaster.Bytes);
        code.Add(0x5a);            // GAS
        code.Add(0xf1);            // CALL
        code.Add(0x50);            // POP
        code.Add(0x00);            // STOP
        code.AddRange(payload);
        return code.ToArray();
    }

    private TransactionResult ExecuteInvalid(Transaction tx, ulong timestamp) =>
        _transactionProcessor.Execute(tx, new BlockExecutionContext(BuildBlock(timestamp, tx).Header, Spec), NullTxTracer.Instance);

    private TxReceipt[] ProcessBlock(ulong timestamp, params Transaction[] transactions)
    {
        Block block = BuildBlock(timestamp, transactions);

        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.StartNewBlockTrace(block);
        foreach (Transaction tx in transactions)
        {
            receiptsTracer.StartNewTxTrace(tx);
            _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), receiptsTracer);
            receiptsTracer.EndTxTrace();
        }
        receiptsTracer.EndBlockTrace();
        return receiptsTracer.TxReceipts.ToArray();
    }

    private static Block BuildBlock(ulong timestamp, params Transaction[] transactions) =>
        Build.A.Block.WithNumber(1)
            .WithTimestamp(timestamp)
            .WithBaseFeePerGas(0)
            .WithTransactions(transactions)
            .WithGasLimit(30_000_000).TestObject;
}
