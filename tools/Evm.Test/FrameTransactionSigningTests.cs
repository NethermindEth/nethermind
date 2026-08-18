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
    private const ulong ChainId = 1; // feeds the sig-hash preimage through Transaction.ChainId

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

    [Test]
    public void Rejects_sender_that_does_not_match_secret_key()
    {
        InputData input = BuildInput(TestItem.PrivateKeyA, TestItem.AddressB, [Entry()]);

        T8nException? exception = Assert.Throws<T8nException>(() => Fill(input));

        Assert.That(exception!.Message, Is.EqualTo("frame transaction sender does not match secretKey"));
    }

    [Test]
    public void Rejects_signer_that_does_not_match_secret_key()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        InputData input = BuildInput(key, key.Address, [Entry(signer: TestItem.AddressB)]);

        T8nException? exception = Assert.Throws<T8nException>(() => Fill(input));

        Assert.That(exception!.Message, Is.EqualTo("frame signature signer does not match secretKey"));
    }

    [Test]
    public void Resolves_sender_from_the_payload_sender_field_without_a_secret_key()
    {
        Transaction transaction = Fill(BuildInput(key: null, sender: null, [Entry()], payloadSender: TestItem.AddressB));

        Assert.That(transaction.SenderAddress, Is.EqualTo(TestItem.AddressB));
    }

    [Test]
    public void Rejects_frame_transaction_without_sender_and_secret_key()
    {
        InputData input = BuildInput(key: null, sender: null, signatures: [Entry()]);

        T8nException? exception = Assert.Throws<T8nException>(() => Fill(input));

        Assert.That(exception!.Message, Is.EqualTo("frame transaction requires a sender or a secretKey"));
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

    // A frame transaction has no gas_limit field, so whatever the JSON carries in `gas` must not decide
    // the limit t8n executes with: it has to match the sum the decoder derives from the frames, or the
    // same transaction runs differently here than in a client that decoded its RLP.
    [TestCase(null, TestName = "gas absent")]
    [TestCase(100_000UL, TestName = "gas conflicts with the frame sum")]
    public void Derives_gas_limit_from_the_frames(ulong? gas)
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, key.Address, [Entry()], gas));

        Assert.That(transaction.GasLimit, Is.EqualTo(50_000UL));
    }

    private static Transaction Fill(InputData input) => input.GetTransactions(null!, ChainId)[0];

    private static FrameSignatureForRpc Entry(byte scheme = TxFrameSignature.SchemeSecp256k1, Address? signer = null,
        byte[]? msg = null, byte[]? signature = null) =>
        new() { Scheme = scheme, Signer = signer, Msg = msg ?? [], Signature = signature ?? [] };

    private static InputData BuildInput(PrivateKey? key, Address? sender, FrameSignatureForRpc[]? signatures,
        ulong? gas = 100_000, Address? payloadSender = null)
    {
        FrameTransactionForRpc transaction = new()
        {
            ChainId = ChainId,
            From = sender,
            Sender = payloadSender,
            Nonce = 0,
            Gas = gas,
            MaxFeePerGas = UInt256.One,
            MaxPriorityFeePerGas = UInt256.Zero,
            Frames =
            [
                new FrameForRpc
                {
                    Mode = TxFrame.ModeVerify,
                    Flags = TxFrame.ApproveExecutionAndPayment,
                    GasLimit = 50_000,
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
