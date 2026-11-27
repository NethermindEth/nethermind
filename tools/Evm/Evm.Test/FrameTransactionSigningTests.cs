// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Evm.T8n.Errors;
using Evm.T8n.JsonTypes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
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
    public void Resolves_sender_and_fills_canonical_witness(Address? sender)
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, sender, [Claim()]));

        Assert.That(transaction.SenderAddress, Is.EqualTo(key.Address));
        AssertWitnessRecoversSigner(transaction, 0, key.Address);
    }

    [Test]
    public void Derives_sender_when_claim_list_is_absent()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, sender: null, claims: null));

        Assert.That(transaction.SenderAddress, Is.EqualTo(key.Address));
    }

    [Test]
    public void Fills_every_secp256k1_witness()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, key.Address, [Claim(), Claim(key: key.Address), Claim(msg: TestItem.KeccakA.BytesToArray())]));

        AssertWitnessRecoversSigner(transaction, 0, key.Address);
        AssertWitnessRecoversSigner(transaction, 1, key.Address);
        AssertWitnessRecoversSigner(transaction, 2, key.Address);
    }

    [Test]
    public void Fills_explicit_key_for_a_contract_sender()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, TestItem.AddressB, [Claim(key: key.Address)]));

        Assert.That(transaction.SenderAddress, Is.EqualTo(TestItem.AddressB));
        AssertWitnessRecoversSigner(transaction, 0, key.Address);
    }

    [Test]
    public void Fills_a_witness_the_decoder_and_validation_both_accept()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, key.Address, [Claim()]));

        Assert.That(FrameTxValidation.IsWellFormed(transaction, assertEnabled: true, out string? error), Is.True, error);

        // The digest is recomputed from the decoded body, so a fill over a stale preimage fails here.
        Transaction decoded = EncodeDecode(transaction);
        decoded.ClaimWitnesses = transaction.ClaimWitnesses;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FrameTxValidation.IsWellFormed(decoded, assertEnabled: true, out error), Is.True, error);
            Assert.That(transaction.Hash, Is.EqualTo(decoded.Hash));
        }
        AssertWitnessRecoversSigner(decoded, 0, key.Address);
    }

    [Test]
    public void Applies_the_state_chain_id_before_signing()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, key.Address, [Claim()], chainId: null));

        Assert.That(transaction.ChainId, Is.EqualTo(ChainId));
        AssertWitnessRecoversSigner(transaction, 0, key.Address);
    }

    [Test]
    public void Preserves_a_supplied_witness_while_filling_the_others()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        byte[] supplied = new byte[Eip8141Constants.Secp256k1WitnessLength];
        supplied[0] = 1;

        Transaction transaction = Fill(BuildInput(key, key.Address, [Claim(), Claim()], witnesses: [supplied, []]));

        Assert.That(transaction.ClaimWitnesses![0], Is.EqualTo(supplied));
        AssertWitnessRecoversSigner(transaction, 1, key.Address);
    }

    [Test]
    public void Leaves_a_custom_claim_without_witness_bytes_alone()
    {
        PrivateKey key = TestItem.PrivateKeyA;

        Transaction transaction = Fill(BuildInput(key, key.Address,
            [new FrameClaimForRpc { Verifier = TestItem.AddressC, Msg = TestItem.KeccakA.BytesToArray(), GasLimit = 10_000 }, Claim()]));

        Assert.That(transaction.ClaimWitnesses![0], Is.Empty);
        AssertWitnessRecoversSigner(transaction, 1, key.Address);
    }

    private static IEnumerable<TestCaseData> InvalidSenderAndKeyCases()
    {
        yield return new TestCaseData(TestItem.PrivateKeyA, TestItem.AddressB, null,
            "frame transaction sender does not match secretKey").SetName("implicit key differs from sender");
        yield return new TestCaseData(TestItem.PrivateKeyA, TestItem.PrivateKeyA.Address, TestItem.AddressB,
            "frame claim key does not match secretKey").SetName("explicit key differs from secretKey");
        yield return new TestCaseData(null, null, null,
            "frame transaction requires a sender or a secretKey").SetName("sender and key absent");
    }

    [TestCaseSource(nameof(InvalidSenderAndKeyCases))]
    public void Rejects_invalid_sender_or_key(PrivateKey? key, Address? sender, Address? claimKey, string expectedMessage)
    {
        InputData input = BuildInput(key, sender, [Claim(key: claimKey)]);

        T8nException? exception = Assert.Throws<T8nException>(() => Fill(input));

        Assert.That(exception!.Message, Is.EqualTo(expectedMessage));
    }

    [Test]
    public void Rejects_an_unfillable_p256_claim()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        InputData input = BuildInput(key, key.Address, [Claim(), new FrameClaimForRpc { Verifier = Eip8141Constants.P256VerifierAddress, Key = TestItem.AddressB.Bytes.ToArray() }]);

        T8nException? exception = Assert.Throws<T8nException>(() => Fill(input));

        Assert.That(exception!.Message, Is.EqualTo("cannot fill frame claim 1: only secp256k1 claims are fillable"));
    }

    private static Transaction Fill(InputData input) => input.GetTransactions(null!, ChainId)[0];

    private static Transaction EncodeDecode(Transaction transaction)
    {
        TxDecoder decoder = TxDecoder.Instance;
        byte[] bytes = new byte[decoder.GetLength(transaction, RlpBehaviors.None)];
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
        decoder.Encode(ref writer, transaction, RlpBehaviors.None);
        RlpReader reader = new(bytes);
        return decoder.Decode(ref reader, RlpBehaviors.None)!;
    }

    private static FrameClaimForRpc Claim(Address? key = null, byte[]? msg = null) =>
        new() { Verifier = Eip8141Constants.Secp256k1VerifierAddress, Key = key?.Bytes.ToArray() ?? [], Msg = msg ?? [] };

    private static InputData BuildInput(PrivateKey? key, Address? sender, FrameClaimForRpc[]? claims,
        ulong? chainId = ChainId, byte[][]? witnesses = null)
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
                    ExecutionGas = 50_000,
                    StateGas = 25_000,
                    Value = UInt256.Zero,
                },
            ],
            Claims = claims,
            Witnesses = witnesses,
        };

        return new InputData
        {
            Txs = [transaction],
            TransactionMetaDataList = [new TransactionMetaData(null, key?.KeyBytes)],
        };
    }

    private static void AssertWitnessRecoversSigner(Transaction transaction, int index, Address expected)
    {
        byte[] raw = transaction.ClaimWitnesses![index];
        Assert.That(raw.Length, Is.EqualTo(Eip8141Constants.Secp256k1WitnessLength));

        Signature signature = new(raw.AsSpan(1), raw[0]);
        TxFrameClaim claim = transaction.FrameClaims![index];
        ValueHash256 hash = claim.ResolveMsg(FrameTxSigHash.ComputeValue(transaction));
        Address? recovered = new EthereumEcdsa(transaction.ChainId!.Value).RecoverAddress(signature, in hash);

        Assert.That(recovered, Is.EqualTo(expected));
    }
}
