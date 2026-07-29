// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    // Middle byte of the DELAY constant (PUSH3 0x015180) in the withdrawal-initiate path — a mutation
    // here changes the code hash without touching the validation path.
    private const int NearMatchMutationOffset = 158;

    private const ulong BlockTimestamp = 1_000_000;
    private const ulong Delay = 86_400;

    // Measured pay-frame validation gas under the prototype fork. Higher than the design doc §5a
    // ~2,150 estimate because this fork carries the EIP-8037/8038 state-gas repricing, which raises
    // the single cold SLOAD; still far under the 15,000 pay-frame bound.
    private const ulong ValidationPathGas = 3_110;

    private static readonly byte[] PaymasterCode = Bytes.FromHexString(PaymasterRuntimeHex);

    private static readonly Address Sender = TestItem.AddressA;
    private static readonly PrivateKey SenderKey = TestItem.PrivateKeyA;
    private static readonly Address Signer = TestItem.PrivateKeyB.Address;
    private static readonly PrivateKey SignerKey = TestItem.PrivateKeyB;
    private static readonly Address Recipient = TestItem.AddressC;
    private static readonly Address Paymaster = TestItem.AddressD;
    private static readonly Address ThirdParty = TestItem.PrivateKeyE.Address;
    private static readonly PrivateKey ThirdPartyKey = TestItem.PrivateKeyE;
    private static readonly Address NonSigner = TestItem.PrivateKeyC.Address;
    private static readonly PrivateKey NonSignerKey = TestItem.PrivateKeyC;
    private static readonly Address NewSigner = TestItem.AddressF;

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

    // 10. A one-opcode mutation keeps the validation path behaving, but its code hash no longer matches
    // the pinned canonical hash — the mempool recognition fixture: recognition fails closed, not behavior.
    [Test]
    public void NearMatchBytecode_BehavesButCodeHashDiffers()
    {
        Assert.That(Keccak.Compute(PaymasterCode), Is.EqualTo(new Hash256(CanonicalCodeHash)),
            "the assembled bytecode must hash to the pinned canonical code hash");

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

    // 11. The pay-frame validation path is a cold SLOAD plus three SIGPARAM reads, dispatch and APPROVE;
    // it sits far under the recommended 15,000 gas pay-frame bound.
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
        TxFrameSignature[] placeholders = new TxFrameSignature[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            placeholders[i] = new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, specs[i].Signer, specs[i].Msg, default);
        }
        tx.FrameSignatures = placeholders;

        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);

        TxFrameSignature[] signed = new TxFrameSignature[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            ValueHash256 digest = specs[i].Msg.Length == 0 ? sigHash : new ValueHash256(specs[i].Msg);
            Signature signature = _ecdsa.Sign(specs[i].Key, in digest);
            signed[i] = new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, specs[i].Signer, specs[i].Msg, ToVrs(signature));
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
