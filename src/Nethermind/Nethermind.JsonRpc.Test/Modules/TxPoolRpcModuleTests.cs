// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public class TxPoolRpcModuleTests
{
    [Test]
    public async Task TxPoolContent_WhenLegacyTxHasNoChainId_SerializesChainIdFromSpec()
    {
        const ulong SomeChainId = 123ul;
        Transaction txA = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithChainId(null)
            .TestObject;
        Transaction txB = Build.A.Transaction
            .WithType(TxType.AccessList)
            .WithAccessList(AccessList.Empty)
            .WithChainId(null)
            .TestObject;

        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetInfo().Returns(new TxPoolInfo(
            pending: new()
            {
                {
                    new AddressAsKey(TestItem.AddressA), new Dictionary<TxPoolTxKey, Transaction>
                    {
                        { new TxPoolTxKey(1), txA }
                    }
                }
            },
            queued: new()
            {
                {
                    new AddressAsKey(TestItem.AddressB), new Dictionary<TxPoolTxKey, Transaction>
                    {
                        { new TxPoolTxKey(2), txB }
                    }
                }
            }
        ));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(SomeChainId);

        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, "txpool_content");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement result = document.RootElement.GetProperty("result");
        JsonElement rpcTxA = result.GetProperty("pending").GetProperty(TestItem.AddressA.ToString(withZeroX: true, withEip55Checksum: true)).GetProperty("1");
        JsonElement rpcTxB = result.GetProperty("queued").GetProperty(TestItem.AddressB.ToString(withZeroX: true, withEip55Checksum: true)).GetProperty("2");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpcTxA.TryGetProperty("chainId", out _), Is.False, "legacy txs without chainId must not have one injected");
            Assert.That(rpcTxB.GetProperty("chainId").GetString(), Is.EqualTo($"0x{SomeChainId:x}"), "EIP-2930 txs without chainId should inherit it from the spec provider");
        }
    }

    [Test]
    public void TxPoolStatus_WhenPoolHasTransactions_ReturnsPendingAndQueuedCounts()
    {
        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetCounts().Returns(new TxPoolCounts(Pending: 2, Queued: 1));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        TxPoolStatus status = txPoolRpcModule.txpool_status().Data;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.Pending, Is.EqualTo(2ul), "the count provider reported 2 pending");
            Assert.That(status.Queued, Is.EqualTo(1ul), "the count provider reported 1 queued");
        }
    }

    [Test]
    public void TxPoolContentFrom_WhenAddressIsInPool_ReturnsOnlyMatchingTransactions()
    {
        const ulong SomeChainId = 1ul;
        Transaction txA = Build.A.Transaction.WithType(TxType.Legacy).WithChainId(null).TestObject;
        Transaction txB = Build.A.Transaction.WithType(TxType.Legacy).WithChainId(null).TestObject;

        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetSenderInfo(TestItem.AddressA).Returns(new TxPoolSenderInfo(
            pending: new Dictionary<TxPoolTxKey, Transaction> { { new TxPoolTxKey(1), txA }, { new TxPoolTxKey(2), txB } },
            queued: new Dictionary<TxPoolTxKey, Transaction>()));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(SomeChainId);

        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        TxPoolContentFrom result = txPoolRpcModule.txpool_contentFrom(TestItem.AddressA).Data;

        Assert.That(result.Pending, Has.Count.EqualTo(2), "AddressA has exactly 2 pending transactions");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pending.ContainsKey(new TxPoolTxKey(1)), Is.True, "nonce 1 belongs to AddressA");
            Assert.That(result.Pending.ContainsKey(new TxPoolTxKey(2)), Is.True, "nonce 2 belongs to AddressA");
            Assert.That(result.Queued, Is.Empty, "no queued transactions were set up for AddressA");
        }
    }

    [Test]
    public void TxPoolContentFrom_WhenAddressNotInPool_ReturnsEmptyPendingAndQueued()
    {
        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetSenderInfo(TestItem.AddressA).Returns(TxPoolSenderInfo.Empty);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(1ul);

        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        TxPoolContentFrom result = txPoolRpcModule.txpool_contentFrom(TestItem.AddressA).Data;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pending, Is.Empty, "the pool has no transactions for any address");
            Assert.That(result.Queued, Is.Empty, "the pool has no transactions for any address");
        }
    }

    [Test]
    public async Task TxPoolStatus_WhenPoolHasTransactions_SerializesCountsAsHexStrings()
    {
        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetCounts().Returns(new TxPoolCounts(Pending: 2, Queued: 1));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, "txpool_status");

        Assert.That(json, Does.Contain("\"pending\":\"0x2\""), "the spec requires pending count as a hex-encoded uint");
        Assert.That(json, Does.Contain("\"queued\":\"0x1\""), "the spec requires queued count as a hex-encoded uint");
    }

    [Test]
    public async Task TxPoolContent_WhenNonceIsLarge_SerializesNonceKeyAsDecimalString()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithNonce(806)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;

        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetInfo().Returns(new TxPoolInfo(
            pending: new()
            {
                {
                    new AddressAsKey(TestItem.AddressA), new Dictionary<TxPoolTxKey, Transaction>
                    {
                        { new TxPoolTxKey(806), tx }
                    }
                }
            },
            queued: []
        ));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(1ul);
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, "txpool_content");

        Assert.That(json, Does.Contain("\"806\":{"), "the spec requires nonce map keys as decimal strings, not hex");
        Assert.That(json, Does.Not.Contain("\"0x326\":{"), "hex nonce keys would violate the spec");
    }

    [Test]
    public async Task TxPoolContent_WhenTransactionIsPending_IncludesNullBlockFieldsAndFrom()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;

        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetInfo().Returns(new TxPoolInfo(
            pending: new()
            {
                {
                    new AddressAsKey(TestItem.AddressA), new Dictionary<TxPoolTxKey, Transaction>
                    {
                        { new TxPoolTxKey(0), tx }
                    }
                }
            },
            queued: []
        ));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(1ul);
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, "txpool_content");

        Assert.That(json, Does.Contain("\"blockHash\":null"), "pending transactions have no block context");
        Assert.That(json, Does.Contain("\"blockNumber\":null"), "pending transactions have no block context");
        Assert.That(json, Does.Contain("\"blockTimestamp\":null"), "pending transactions have no block context");
        Assert.That(json, Does.Contain("\"transactionIndex\":null"), "pending transactions have no block context");
        Assert.That(json, Does.Contain("\"from\":\"" + TestItem.AddressA.ToString().ToLowerInvariant() + "\""), "the spec requires 'from' to be present on every pending transaction");
        Assert.That(json, Does.Contain("\"" + TestItem.AddressA.ToString(withZeroX: true, withEip55Checksum: true) + "\":{"), "address map keys must use EIP-55 checksum format to match the spec");
    }

    [TestCase("txpool_content", TxPoolContentJson)]
    [TestCase("txpool_inspect", TxPoolInspectJson)]
    public async Task TxPool_WhenPoolHasSeveralSendersAndNonces_SerializesToTheContractedBytes(string method, string expectedJson)
    {
        static Transaction Tx(TxType type, ulong nonce, Address sender, Hash256 hash) => Build.A.Transaction
            .WithType(type)
            .WithNonce(nonce)
            .WithSenderAddress(sender)
            .WithTo(TestItem.AddressF)
            .WithValue(nonce + 1)
            .WithGasLimit(21_000 + nonce)
            .WithGasPrice(2)
            .WithMaxFeePerGas(10 + nonce)
            .WithChainId(1)
            .WithHash(hash)
            .TestObject;

        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetInfo().Returns(new TxPoolInfo(
            pending: new()
            {
                {
                    new AddressAsKey(TestItem.AddressA), new Dictionary<TxPoolTxKey, Transaction>
                    {
                        { new TxPoolTxKey(0), Tx(TxType.Legacy, 0, TestItem.AddressA, TestItem.KeccakA) },
                        { new TxPoolTxKey(1), Tx(TxType.EIP1559, 1, TestItem.AddressA, TestItem.KeccakB) },
                    }
                },
                {
                    new AddressAsKey(TestItem.AddressB), new Dictionary<TxPoolTxKey, Transaction>
                    {
                        { new TxPoolTxKey(5), Tx(TxType.AccessList, 5, TestItem.AddressB, TestItem.KeccakC) },
                    }
                },
            },
            queued: new()
            {
                {
                    new AddressAsKey(TestItem.AddressA), new Dictionary<TxPoolTxKey, Transaction>
                    {
                        { new TxPoolTxKey(3), Tx(TxType.EIP1559, 3, TestItem.AddressA, TestItem.KeccakD) },
                    }
                },
                {
                    new AddressAsKey(TestItem.AddressC), new Dictionary<TxPoolTxKey, Transaction>
                    {
                        { new TxPoolTxKey(7), Tx(TxType.Legacy, 7, TestItem.AddressC, TestItem.KeccakE) },
                        { new TxPoolTxKey(TestItem.KeccakG), Tx(TxType.Legacy, 9, TestItem.AddressC, TestItem.KeccakG) },
                    }
                },
            }));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(1ul);
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, method);

        Assert.That(json, Is.EqualTo(expectedJson));
    }

    private const string TxPoolContentJson =
        """
        {"jsonrpc":"2.0","result":{"pending":{"0xb7705aE4c6F81B66cdB323C65f4E8133690fC099":{"0":{"type":"0x0","nonce":"0x0","to":"0x65942aaf2c32a1aca4f14e82e94fce91960893a2","from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","value":"0x1","input":"0x","gasPrice":"0x2","chainId":"0x1","v":"0x0","r":"0x0","s":"0x0","hash":"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760","transactionIndex":null,"blockHash":null,"blockNumber":null,"blockTimestamp":null,"gas":"0x5208"},"1":{"type":"0x2","maxPriorityFeePerGas":"0x2","maxFeePerGas":"0xb","accessList":[],"yParity":"0x0","chainId":"0x1","nonce":"0x1","to":"0x65942aaf2c32a1aca4f14e82e94fce91960893a2","from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","value":"0x2","input":"0x","gasPrice":"0xb","v":"0x0","r":"0x0","s":"0x0","hash":"0x1f675bff07515f5df96737194ea945c36c41e7b4fcef307b7cd4d0e602a69111","transactionIndex":null,"blockHash":null,"blockNumber":null,"blockTimestamp":null,"gas":"0x5209"}},"0x942921b14f1B1C385CD7e0cc2eF7aBe5598c8358":{"5":{"type":"0x1","accessList":[],"yParity":"0x0","chainId":"0x1","nonce":"0x5","to":"0x65942aaf2c32a1aca4f14e82e94fce91960893a2","from":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358","value":"0x6","input":"0x","gasPrice":"0x2","v":"0x0","r":"0x0","s":"0x0","hash":"0x017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72","transactionIndex":null,"blockHash":null,"blockNumber":null,"blockTimestamp":null,"gas":"0x520d"}}},"queued":{"0xb7705aE4c6F81B66cdB323C65f4E8133690fC099":{"3":{"type":"0x2","maxPriorityFeePerGas":"0x2","maxFeePerGas":"0xd","accessList":[],"yParity":"0x0","chainId":"0x1","nonce":"0x3","to":"0x65942aaf2c32a1aca4f14e82e94fce91960893a2","from":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099","value":"0x4","input":"0x","gasPrice":"0xd","v":"0x0","r":"0x0","s":"0x0","hash":"0x6c3fd336b49dcb1c57dd4fbeaf5f898320b0da06a5ef64e798c6497600bb79f2","transactionIndex":null,"blockHash":null,"blockNumber":null,"blockTimestamp":null,"gas":"0x520b"}},"0x76e68A8696537e4141926F3e528733af9e237d69":{"7":{"type":"0x0","nonce":"0x7","to":"0x65942aaf2c32a1aca4f14e82e94fce91960893a2","from":"0x76e68a8696537e4141926f3e528733af9e237d69","value":"0x8","input":"0x","gasPrice":"0x2","chainId":"0x1","v":"0x0","r":"0x0","s":"0x0","hash":"0x434b529473163ef4ed9c9341d9b7250ab9183c27e7add004c3bba38c56274e24","transactionIndex":null,"blockHash":null,"blockNumber":null,"blockTimestamp":null,"gas":"0x520f"},"0x077da99d806abd13c9f15ece5398525119d11e11e9836b2ee7d23f6159ad87d2":{"type":"0x0","nonce":"0x9","to":"0x65942aaf2c32a1aca4f14e82e94fce91960893a2","from":"0x76e68a8696537e4141926f3e528733af9e237d69","value":"0xa","input":"0x","gasPrice":"0x2","chainId":"0x1","v":"0x0","r":"0x0","s":"0x0","hash":"0x077da99d806abd13c9f15ece5398525119d11e11e9836b2ee7d23f6159ad87d2","transactionIndex":null,"blockHash":null,"blockNumber":null,"blockTimestamp":null,"gas":"0x5211"}}}},"id":67}
        """;

    private const string TxPoolInspectJson =
        """
        {"jsonrpc":"2.0","result":{"pending":{"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099":{"0":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099: 1 wei + 21000 × 2 gas","1":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099: 2 wei + 21001 × 2 gas"},"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358":{"5":"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358: 6 wei + 21005 × 2 gas"}},"queued":{"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099":{"3":"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099: 4 wei + 21003 × 2 gas"},"0x76e68a8696537e4141926f3e528733af9e237d69":{"7":"0x76e68a8696537e4141926f3e528733af9e237d69: 8 wei + 21007 × 2 gas","0x077da99d806abd13c9f15ece5398525119d11e11e9836b2ee7d23f6159ad87d2":"0x76e68a8696537e4141926f3e528733af9e237d69: 10 wei + 21009 × 2 gas"}}},"id":67}
        """;

    [Test]
    public async Task TxPoolContentFrom_WhenNonceIsLarge_SerializesNonceKeyAsDecimalString()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithNonce(806)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;

        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetSenderInfo(TestItem.AddressA).Returns(new TxPoolSenderInfo(
            pending: new Dictionary<TxPoolTxKey, Transaction> { { new TxPoolTxKey(806), tx } },
            queued: new Dictionary<TxPoolTxKey, Transaction>()));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(1ul);
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, "txpool_contentFrom", TestItem.AddressA);

        Assert.That(json, Does.Contain("\"806\":{"), "the spec requires nonce map keys as decimal strings, not hex");
        Assert.That(json, Does.Not.Contain("\"0x326\":{"), "hex nonce keys would violate the spec");
    }

    [Test]
    public async Task TxPoolContentFrom_WhenAddressNotInPool_SerializesEmptyPendingAndQueued()
    {
        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetSenderInfo(TestItem.AddressA).Returns(TxPoolSenderInfo.Empty);

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(1ul);
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, "txpool_contentFrom", TestItem.AddressA);

        Assert.That(json, Does.Contain("\"pending\":{}"), "spec requires the pending field to always be present");
        Assert.That(json, Does.Contain("\"queued\":{}"), "spec requires the queued field to always be present");
    }
}
