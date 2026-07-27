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
    [Test]
    public void GetTransactions_preserves_explicit_sender_and_fills_canonical_signature()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        InputData input = BuildInput(key, key.Address, []);

        Transaction transaction = input.GetTransactions(null!, 1)[0];

        Assert.That(transaction.SenderAddress, Is.EqualTo(key.Address));
        Assert.That(transaction.FrameSignatures![0].Signature.Length, Is.EqualTo(TxFrameSignature.Secp256k1SignatureLength));
        AssertSignatureRecoversSender(transaction);
    }

    [Test]
    public void GetTransactions_derives_missing_sender_from_secret_key()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        InputData input = BuildInput(key, null, []);

        Transaction transaction = input.GetTransactions(null!, 1)[0];

        Assert.That(transaction.SenderAddress, Is.EqualTo(key.Address));
        AssertSignatureRecoversSender(transaction);
    }

    [Test]
    public void GetTransactions_preserves_pre_signed_canonical_signature()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        byte[] signature = new byte[TxFrameSignature.Secp256k1SignatureLength];
        signature[0] = 1;
        InputData input = BuildInput(key, key.Address, signature);

        Transaction transaction = input.GetTransactions(null!, 1)[0];

        Assert.That(transaction.FrameSignatures![0].Signature.ToArray(), Is.EqualTo(signature));
    }

    [Test]
    public void GetTransactions_rejects_sender_that_does_not_match_secret_key()
    {
        InputData input = BuildInput(TestItem.PrivateKeyA, TestItem.AddressB, []);

        T8nException? exception = Assert.Throws<T8nException>(() => input.GetTransactions(null!, 1));

        Assert.That(exception!.Message, Is.EqualTo("frame transaction sender does not match secretKey"));
    }

    private static InputData BuildInput(PrivateKey key, Address? sender, byte[] signature)
    {
        FrameTransactionForRpc transaction = new()
        {
            ChainId = 1,
            From = sender,
            Nonce = 0,
            Gas = 100_000,
            MaxFeePerGas = UInt256.One,
            MaxPriorityFeePerGas = UInt256.Zero,
            Frames =
            [
                new FrameForRpc
                {
                    Mode = TxFrame.ModeVerify,
                    Flags = TxFrame.ApproveExecutionAndPayment,
                    ExecutionGasLimit = 50_000,
                    Value = UInt256.Zero,
                },
            ],
            Signatures =
            [
                new FrameSignatureForRpc
                {
                    Scheme = TxFrameSignature.SchemeSecp256k1,
                    Signature = signature,
                },
            ],
        };

        return new InputData
        {
            Txs = [transaction],
            TransactionMetaDataList = [new TransactionMetaData(null, key.KeyBytes)],
        };
    }

    private static void AssertSignatureRecoversSender(Transaction transaction)
    {
        ReadOnlySpan<byte> raw = transaction.FrameSignatures![0].Signature.Span;
        Signature signature = new(raw[1..], raw[0]);
        ValueHash256 hash = FrameTxSigHash.ComputeValue(transaction);
        Address? recovered = new EthereumEcdsa(transaction.ChainId!.Value).RecoverAddress(signature, in hash);

        Assert.That(recovered, Is.EqualTo(transaction.SenderAddress));
    }
}
