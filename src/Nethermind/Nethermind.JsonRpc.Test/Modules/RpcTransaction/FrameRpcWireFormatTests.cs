// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.Serialization.Json;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.RpcTransaction;

/// <summary>Pins the bytes <c>eth_getTransactionByHash</c> and <c>txpool_content</c> emit for a frame transaction.</summary>
/// <remarks>These DTOs are the wire contract external tooling and the t8n fixture reader parse, so the property
/// types behind them may change only while the emitted text does not.</remarks>
[TestFixture]
public class FrameRpcWireFormatTests
{
    private const string PopulatedFrameTxJson =
        """
        {"type":"0x6","frames":[{"mode":1,"flags":3,"target":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","executionGasLimit":"0xc350","stateGasLimit":"0x3e8","value":"0x0","data":"0x01020304"},{"mode":0,"flags":4,"executionGasLimit":"0x5208","stateGasLimit":"0x0","value":"0x5","data":"0x"}],"signatures":[{"scheme":1,"signer":"0x76e68a8696537e4141926f3e528733af9e237d69","msg":"0x0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20","signature":"0xfffefdfcfbfaf9f8f7f6f5f4f3f2f1f0efeeedecebeae9e8e7e6e5e4e3e2e1e0dfdedddcdbdad9d8d7d6d5d4d3d2d1d0cfcecdcccbcac9c8c7c6c5c4c3c2c1c0bf"},{"scheme":0,"msg":"0x","signature":"0xaabb"}],"maxFeePerBlobGas":"0x0","blobVersionedHashes":[],"maxPriorityFeePerGas":"0x1","maxFeePerGas":"0x64","accessList":[],"yParity":"0x0","chainId":"0x1","nonce":"0x326","to":null,"from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","value":"0x0","input":"0x","gasPrice":"0x64","v":"0x0","r":"0x0","s":"0x0","hash":"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760","transactionIndex":null,"blockHash":null,"blockNumber":null,"blockTimestamp":null,"gas":"0xf4240"}
        """;

    private const string TxPoolContentJson =
        """
        {"pending":{"0xb7705aE4c6F81B66cdB323C65f4E8133690fC099":{"806":{"type":"0x6","frames":[{"mode":1,"flags":3,"target":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","executionGasLimit":"0xc350","stateGasLimit":"0x3e8","value":"0x0","data":"0x01020304"},{"mode":0,"flags":4,"executionGasLimit":"0x5208","stateGasLimit":"0x0","value":"0x5","data":"0x"}],"signatures":[{"scheme":1,"signer":"0x76e68a8696537e4141926f3e528733af9e237d69","msg":"0x0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20","signature":"0xfffefdfcfbfaf9f8f7f6f5f4f3f2f1f0efeeedecebeae9e8e7e6e5e4e3e2e1e0dfdedddcdbdad9d8d7d6d5d4d3d2d1d0cfcecdcccbcac9c8c7c6c5c4c3c2c1c0bf"},{"scheme":0,"msg":"0x","signature":"0xaabb"}],"maxFeePerBlobGas":"0x0","blobVersionedHashes":[],"maxPriorityFeePerGas":"0x1","maxFeePerGas":"0x64","accessList":[],"yParity":"0x0","chainId":"0x1","nonce":"0x326","to":null,"from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","value":"0x0","input":"0x","gasPrice":"0x64","v":"0x0","r":"0x0","s":"0x0","hash":"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760","transactionIndex":null,"blockHash":null,"blockNumber":null,"blockTimestamp":null,"gas":"0xf4240"},"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111":{"type":"0x6","frames":[{"mode":1,"flags":3,"target":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","executionGasLimit":"0xc350","stateGasLimit":"0x3e8","value":"0x0","data":"0x01020304"},{"mode":0,"flags":4,"executionGasLimit":"0x5208","stateGasLimit":"0x0","value":"0x5","data":"0x"}],"signatures":[{"scheme":1,"signer":"0x76e68a8696537e4141926f3e528733af9e237d69","msg":"0x0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20","signature":"0xfffefdfcfbfaf9f8f7f6f5f4f3f2f1f0efeeedecebeae9e8e7e6e5e4e3e2e1e0dfdedddcdbdad9d8d7d6d5d4d3d2d1d0cfcecdcccbcac9c8c7c6c5c4c3c2c1c0bf"},{"scheme":0,"msg":"0x","signature":"0xaabb"}],"maxFeePerBlobGas":"0x0","blobVersionedHashes":[],"maxPriorityFeePerGas":"0x1","maxFeePerGas":"0x64","accessList":[],"yParity":"0x0","chainId":"0x1","nonce":"0x326","to":null,"from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","value":"0x0","input":"0x","gasPrice":"0x64","v":"0x0","r":"0x0","s":"0x0","hash":"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111","transactionIndex":null,"blockHash":null,"blockNumber":null,"blockTimestamp":null,"gas":"0xf4240"}}},"queued":{}}
        """;

    private static readonly EthereumJsonSerializer Serializer = new();

    private static Transaction BuildPopulatedFrameTx()
    {
        byte[] msg = new byte[32];
        for (int i = 0; i < msg.Length; i++) msg[i] = (byte)(i + 1);
        byte[] signature = new byte[65];
        for (int i = 0; i < signature.Length; i++) signature[i] = (byte)(0xff - i);

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            Nonce = 806,
            SenderAddress = TestItem.AddressA,
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 100,
            Frames =
            [
                new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, TestItem.AddressB, 50_000, 1_000, (UInt256)0, new byte[] { 0x01, 0x02, 0x03, 0x04 }),
                new TxFrame(FrameMode.Default, FrameFlags.AtomicBatch, null, 21_000, 0, (UInt256)5, ReadOnlyMemory<byte>.Empty),
            ],
            FrameSignatures =
            [
                new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressC, msg, signature),
                new TxFrameSignature(TxFrameSignature.SchemeArbitrary, null, default, new byte[] { 0xaa, 0xbb }),
            ],
        };
        tx.Hash = TestItem.KeccakA;
        return tx;
    }

    [Test]
    public void FrameTransaction_SerializesToTheContractedBytes()
    {
        TransactionForRpc rpc = TransactionForRpc.FromTransaction(BuildPopulatedFrameTx());

        Assert.That(Serializer.Serialize(rpc), Is.EqualTo(PopulatedFrameTxJson));
    }

    [Test]
    public void TxPoolContent_SerializesToTheContractedBytes()
    {
        Transaction byNonce = BuildPopulatedFrameTx();
        Transaction byHash = BuildPopulatedFrameTx();
        byHash.Hash = TestItem.KeccakB;

        TxPoolInfo info = new(
            pending: new()
            {
                {
                    new AddressAsKey(TestItem.AddressA), new Dictionary<TxPoolTxKey, Transaction>
                    {
                        { new TxPoolTxKey(806), byNonce },
                        { new TxPoolTxKey(TestItem.KeccakB), byHash },
                    }
                }
            },
            queued: []);

        Assert.That(Serializer.Serialize(new TxPoolContent(info, 1)), Is.EqualTo(TxPoolContentJson));
    }

    [Test]
    public void FrameTransaction_WhenByteFieldsAreJsonNull_ReadsThemAsEmpty()
    {
        const string Json = """{"type":"0x6","frames":[{"mode":0,"flags":0,"executionGasLimit":"0x1","stateGasLimit":"0x0","value":"0x0","data":null}],"signatures":[{"scheme":0,"msg":null,"signature":null}]}""";

        FrameTransactionForRpc rpc = (FrameTransactionForRpc)Serializer.Deserialize<TransactionForRpc>(Json)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpc.Frames![0].Data.IsEmpty, Is.True);
            Assert.That(rpc.Signatures![0].Msg.IsEmpty, Is.True);
            Assert.That(rpc.Signatures[0].Signature.IsEmpty, Is.True);
        }
    }

    [TestCase(806ul)]
    [TestCase(ulong.MaxValue)]
    public void TxPoolTxKey_SerializesAsTheRenderedKeyItReplaced(ulong nonce)
    {
        Dictionary<AddressAsKey, Dictionary<string, string>> rendered = new()
        {
            { TestItem.AddressA, new() { { nonce.ToString(CultureInfo.InvariantCulture), "x" }, { TestItem.KeccakB.ToString(), "y" } } }
        };
        Dictionary<AddressAsKey, Dictionary<TxPoolTxKey, string>> keyed = new()
        {
            { TestItem.AddressA, new() { { new TxPoolTxKey(nonce), "x" }, { new TxPoolTxKey(TestItem.KeccakB), "y" } } }
        };

        Assert.That(Serializer.Serialize(keyed), Is.EqualTo(Serializer.Serialize(rendered)));
    }

    [Test]
    public void TxPoolTxKey_RoundTripsThroughItsJsonForm()
    {
        Dictionary<TxPoolTxKey, string> source = new()
        {
            { new TxPoolTxKey(806), "x" },
            { new TxPoolTxKey(TestItem.KeccakB), "y" },
        };

        Dictionary<TxPoolTxKey, string> roundTripped = Serializer.Deserialize<Dictionary<TxPoolTxKey, string>>(Serializer.Serialize(source))!;

        Assert.That(roundTripped, Is.EqualTo(source));
    }
}
