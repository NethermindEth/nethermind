// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Evm.T8n.Errors;
using Evm.T8n.JsonTypes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Evm.Test;

[TestFixture]
public class FrameTransactionSigningTests
{
    private const ulong ChainId = 1;

    private static IEnumerable<TestCaseData> SenderResolutionCases()
    {
        yield return new TestCaseData(TestItem.PrivateKeyA.Address).SetName("explicit sender preserved");
        yield return new TestCaseData(null).SetName("sender derived from secretKey");
    }

    [TestCaseSource(nameof(SenderResolutionCases))]
    public void Resolves_sender_and_fills_canonical_signature(Address? sender)
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, sender, [Entry()]));

        Assert.That(transaction.SenderAddress, Is.EqualTo(key.Address));
        AssertSignatureRecoversSigner(transaction, 0, key.Address);
    }

    [Test]
    public void Derives_sender_when_signature_list_is_absent()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, sender: null, signatures: null));

        Assert.That(transaction.SenderAddress, Is.EqualTo(key.Address));
    }

    [Test]
    public void Fills_every_canonical_signature()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, key.Address, [Entry(), Entry(signer: key.Address)]));

        AssertSignatureRecoversSigner(transaction, 0, key.Address);
        AssertSignatureRecoversSigner(transaction, 1, key.Address);
    }

    [Test]
    public void Fills_explicit_signer_for_a_contract_sender()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, TestItem.AddressB, [Entry(signer: key.Address)]));

        Assert.That(transaction.SenderAddress, Is.EqualTo(TestItem.AddressB));
        AssertSignatureRecoversSigner(transaction, 0, key.Address);
    }

    [Test]
    public void Fills_a_signature_the_decoder_and_validation_both_accept()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, key.Address, [Entry()]));

        Assert.That(FrameTxValidation.IsWellFormed(transaction, postTxEnabled: true, out string? error), Is.True, error);

        // The digest is recomputed from the decoded form, so a fill over a stale preimage fails here.
        Transaction decoded = EncodeDecode(transaction);

        Assert.That(FrameTxValidation.IsWellFormed(decoded, postTxEnabled: true, out error), Is.True, error);
        Assert.That(transaction.Hash, Is.EqualTo(decoded.Hash));
        AssertSignatureRecoversSigner(decoded, 0, key.Address);
    }

    [Test]
    public void Applies_the_state_chain_id_before_signing()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, key.Address, [Entry()], chainId: null));

        Assert.That(transaction.ChainId, Is.EqualTo(ChainId));
        AssertSignatureRecoversSigner(transaction, 0, key.Address);
    }

    [Test]
    public void Preserves_pre_signed_canonical_signature()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        byte[] signature = new byte[TxFrameSignature.Secp256k1SignatureLength];
        signature[0] = 1;

        Transaction transaction = Fill(BuildInput(key, key.Address, [Entry(signature: signature)]));

        Assert.That(transaction.FrameSignatures![0].Signature.ToArray(), Is.EqualTo(signature));
    }

    [Test]
    public void Preserves_explicit_digest_signature_while_filling_canonical_entries()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        byte[] explicitSignature = new byte[TxFrameSignature.Secp256k1SignatureLength];
        explicitSignature[0] = 1;
        FrameSignatureForRpc explicitEntry = Entry(msg: TestItem.KeccakA.BytesToArray(), signature: explicitSignature);

        Transaction transaction = Fill(BuildInput(key, key.Address, [explicitEntry, Entry()]));

        Assert.That(transaction.FrameSignatures![0].Signature.ToArray(), Is.EqualTo(explicitSignature));
        AssertSignatureRecoversSigner(transaction, 1, key.Address);
    }

    [Test]
    public void Skips_arbitrary_signature_entry()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, key.Address, [Entry(TxFrameSignature.SchemeArbitrary), Entry()]));

        Assert.That(transaction.FrameSignatures![0].Signature.IsEmpty, Is.True);
        AssertSignatureRecoversSigner(transaction, 1, key.Address);
    }

    private static IEnumerable<TestCaseData> InvalidSenderAndSignerCases()
    {
        yield return new TestCaseData(TestItem.PrivateKeyA, TestItem.AddressB, null,
            "frame transaction sender does not match secretKey").SetName("implicit signer differs from sender");
        yield return new TestCaseData(TestItem.PrivateKeyA, TestItem.PrivateKeyA.Address, TestItem.AddressB,
            "frame signature signer does not match secretKey").SetName("explicit signer differs from key");
        yield return new TestCaseData(null, null, null,
            "frame transaction requires a sender or a secretKey").SetName("sender and key absent");
    }

    [TestCaseSource(nameof(InvalidSenderAndSignerCases))]
    public void Rejects_invalid_sender_or_signer(PrivateKey? key, Address? sender, Address? signer, string expectedMessage)
    {
        InputData input = BuildInput(key, sender, [Entry(signer: signer)]);

        T8nException? exception = Assert.Throws<T8nException>(() => Fill(input));

        Assert.That(exception!.Message, Is.EqualTo(expectedMessage));
    }

    private static IEnumerable<TestCaseData> UnfillableSignatureCases()
    {
        yield return new TestCaseData(Entry(msg: TestItem.KeccakA.BytesToArray())).SetName("explicit digest secp256k1");
        yield return new TestCaseData(Entry(TxFrameSignature.SchemeP256)).SetName("P256");
    }

    [TestCaseSource(nameof(UnfillableSignatureCases))]
    public void Rejects_unfillable_signature_entry(FrameSignatureForRpc entry)
    {
        PrivateKey key = TestItem.PrivateKeyA;
        InputData input = BuildInput(key, key.Address, [Entry(), entry]);

        T8nException? exception = Assert.Throws<T8nException>(() => Fill(input));

        Assert.That(exception!.Message,
            Is.EqualTo("cannot fill frame signature 1: only canonical-hash secp256k1 entries are fillable"));
    }

    private static Transaction Fill(InputData input) => input.GetTransactions(null!, ChainId)[0];

    private static Transaction EncodeDecode(Transaction transaction)
    {
        TxDecoder decoder = TxDecoder.Instance;
        byte[] bytes = new byte[decoder.GetLength(transaction, RlpBehaviors.None)];
        RlpWriter writer = new(bytes);
        decoder.Encode(ref writer, transaction, RlpBehaviors.None);
        RlpReader reader = new(bytes);
        return decoder.Decode(ref reader, RlpBehaviors.None)!;
    }

    private static FrameSignatureForRpc Entry(byte scheme = TxFrameSignature.SchemeSecp256k1, Address? signer = null,
        byte[]? msg = null, byte[]? signature = null) =>
        new() { Scheme = scheme, Signer = signer, Msg = msg ?? [], Signature = signature ?? [] };

    private static InputData BuildInput(PrivateKey? key, Address? sender, FrameSignatureForRpc[]? signatures,
        ulong? chainId = ChainId)
    {
        FrameTransactionForRpc transaction = new()
        {
            ChainId = chainId,
            From = sender,
            Nonce = 0,
            Gas = 100_000,
            MaxFeePerGas = UInt256.One,
            MaxPriorityFeePerGas = UInt256.Zero,
            Frames =
            [
                new FrameForRpc
                {
                    Mode = (byte)FrameMode.Verify,
                    Flags = (byte)FrameFlags.ApproveExecutionAndPayment,
                    ExecutionGasLimit = 50_000,
                    StateGasLimit = 25_000,
                    Value = UInt256.Zero,
                },
            ],
            Signatures = signatures,
        };

        return new InputData
        {
            Txs = [transaction],
            TransactionMetaDataList = [new TransactionMetaData(null, key?.KeyBytes)],
        };
    }

    private static void AssertSignatureRecoversSigner(Transaction transaction, int index, Address expected)
    {
        ReadOnlySpan<byte> raw = transaction.FrameSignatures![index].Signature.Span;
        Assert.That(raw.Length, Is.EqualTo(TxFrameSignature.Secp256k1SignatureLength));

        Signature signature = new(raw[1..], raw[0]);
        ValueHash256 hash = FrameTxSigHash.ComputeValue(transaction);
        Address? recovered = new EthereumEcdsa(transaction.ChainId!.Value).RecoverAddress(signature, in hash);

        Assert.That(recovered, Is.EqualTo(expected));
    }
}
