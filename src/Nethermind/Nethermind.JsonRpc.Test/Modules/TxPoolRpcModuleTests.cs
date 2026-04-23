// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public class TxPoolRpcModuleTests
{
    [Test]
    public void TxPoolContent_WhenLegacyTxHasNoChainId_SerializesChainIdFromSpec()
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
                    new AddressAsKey(TestItem.AddressA), new Dictionary<ulong, Transaction>
                    {
                        { 1, txA }
                    }
                }
            },
            queued: new()
            {
                {
                    new AddressAsKey(TestItem.AddressB), new Dictionary<ulong, Transaction>
                    {
                        { 2, txB }
                    }
                }
            }
        ));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(SomeChainId);

        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        TxPoolContent txpoolContent = txPoolRpcModule.txpool_content().Data;

        LegacyTransactionForRpc? rpcTxA = txpoolContent.Pending[TestItem.AddressA.ToString(withZeroX: true, withEip55Checksum: true)][1] as LegacyTransactionForRpc;
        AccessListTransactionForRpc? rpcTxB = txpoolContent.Queued[TestItem.AddressB.ToString(withZeroX: true, withEip55Checksum: true)][2] as AccessListTransactionForRpc;

        rpcTxA!.ChainId.Should().BeNull("legacy txs without chainId must not have one injected");
        rpcTxB!.ChainId.Should().Be(SomeChainId, "EIP-2930 txs without chainId should inherit it from the spec provider");
    }

    [Test]
    public void TxPoolStatus_WhenPoolHasTransactions_ReturnsPendingAndQueuedCounts()
    {
        Transaction txA = Build.A.Transaction.WithType(TxType.Legacy).TestObject;
        Transaction txB = Build.A.Transaction.WithType(TxType.Legacy).TestObject;

        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetInfo().Returns(new TxPoolInfo(
            pending: new()
            {
                {
                    new AddressAsKey(TestItem.AddressA), new Dictionary<ulong, Transaction>
                    {
                        { 1, txA }, { 2, txB }
                    }
                }
            },
            queued: new()
            {
                {
                    new AddressAsKey(TestItem.AddressB), new Dictionary<ulong, Transaction>
                    {
                        { 5, txA }
                    }
                }
            }
        ));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        TxPoolStatus status = txPoolRpcModule.txpool_status().Data;

        status.Pending.Should().Be(2ul, "AddressA has 2 pending transactions");
        status.Queued.Should().Be(1ul, "AddressB has 1 queued transaction");
    }

    [Test]
    public void TxPoolContentFrom_WhenAddressIsInPool_ReturnsOnlyMatchingTransactions()
    {
        // AddressA has nonces 1 and 2 in pending; AddressB has nonce 3.
        // Querying for AddressA must return only its 2 transactions and an empty queued map.
        const ulong SomeChainId = 1ul;
        Transaction txA = Build.A.Transaction.WithType(TxType.Legacy).WithChainId(null).TestObject;
        Transaction txB = Build.A.Transaction.WithType(TxType.Legacy).WithChainId(null).TestObject;
        Transaction txC = Build.A.Transaction.WithType(TxType.Legacy).WithChainId(null).TestObject;

        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetInfo().Returns(new TxPoolInfo(
            pending: new()
            {
                {
                    new AddressAsKey(TestItem.AddressA), new Dictionary<ulong, Transaction>
                    {
                        { 1, txA }, { 2, txB }
                    }
                },
                {
                    new AddressAsKey(TestItem.AddressB), new Dictionary<ulong, Transaction>
                    {
                        { 3, txC }
                    }
                }
            },
            queued: new()
        ));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(SomeChainId);

        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        TxPoolContentFrom result = txPoolRpcModule.txpool_contentFrom(TestItem.AddressA).Data;

        result.Pending.Should().HaveCount(2, "AddressA has exactly 2 pending transactions");
        result.Pending.Should().ContainKey(1ul, "nonce 1 belongs to AddressA");
        result.Pending.Should().ContainKey(2ul, "nonce 2 belongs to AddressA");
        result.Queued.Should().BeEmpty("no queued transactions were set up for AddressA");
    }

    [Test]
    public void TxPoolContentFrom_WhenAddressNotInPool_ReturnsEmptyPendingAndQueued()
    {
        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetInfo().Returns(new TxPoolInfo(
            pending: new(),
            queued: new()
        ));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(1ul);

        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        TxPoolContentFrom result = txPoolRpcModule.txpool_contentFrom(TestItem.AddressA).Data;

        result.Pending.Should().BeEmpty("the pool has no transactions for any address");
        result.Queued.Should().BeEmpty("the pool has no transactions for any address");
    }

    [Test]
    public async Task TxPoolStatus_WhenPoolHasTransactions_SerializesCountsAsHexStrings()
    {
        Transaction txA = Build.A.Transaction.WithType(TxType.Legacy).TestObject;
        Transaction txB = Build.A.Transaction.WithType(TxType.Legacy).TestObject;

        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetInfo().Returns(new TxPoolInfo(
            pending: new()
            {
                {
                    new AddressAsKey(TestItem.AddressA), new Dictionary<ulong, Transaction>
                    {
                        { 1, txA }, { 2, txB }
                    }
                }
            },
            queued: new()
            {
                {
                    new AddressAsKey(TestItem.AddressB), new Dictionary<ulong, Transaction>
                    {
                        { 5, txA }
                    }
                }
            }
        ));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, "txpool_status");

        json.Should().Contain("\"pending\":\"0x2\"", "the spec requires pending count as a hex-encoded uint");
        json.Should().Contain("\"queued\":\"0x1\"", "the spec requires queued count as a hex-encoded uint");
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
                    new AddressAsKey(TestItem.AddressA), new Dictionary<ulong, Transaction>
                    {
                        { 806, tx }
                    }
                }
            },
            queued: new()
        ));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(1ul);
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, "txpool_content");

        json.Should().Contain("\"806\":{", "the spec requires nonce map keys as decimal strings, not hex");
        json.Should().NotContain("\"0x326\":{", "hex nonce keys would violate the spec");
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
                    new AddressAsKey(TestItem.AddressA), new Dictionary<ulong, Transaction>
                    {
                        { 0, tx }
                    }
                }
            },
            queued: new()
        ));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(1ul);
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, "txpool_content");

        json.Should().Contain("\"blockHash\":null", "pending transactions have no block context");
        json.Should().Contain("\"blockNumber\":null", "pending transactions have no block context");
        json.Should().Contain("\"blockTimestamp\":null", "pending transactions have no block context");
        json.Should().Contain("\"transactionIndex\":null", "pending transactions have no block context");
        json.Should().Contain("\"from\":\"" + TestItem.AddressA.ToString().ToLowerInvariant() + "\"",
            "the spec requires 'from' to be present on every pending transaction");
        json.Should().Contain("\"" + TestItem.AddressA.ToString(withZeroX: true, withEip55Checksum: true) + "\":{",
            "address map keys must use EIP-55 checksum format to match the spec");
    }

    [Test]
    public async Task TxPoolContentFrom_WhenNonceIsLarge_SerializesNonceKeyAsDecimalString()
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
                    new AddressAsKey(TestItem.AddressA), new Dictionary<ulong, Transaction>
                    {
                        { 806, tx }
                    }
                }
            },
            queued: new()
        ));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(1ul);
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, "txpool_contentFrom", TestItem.AddressA);

        json.Should().Contain("\"806\":{", "the spec requires nonce map keys as decimal strings, not hex");
        json.Should().NotContain("\"0x326\":{", "hex nonce keys would violate the spec");
    }

    [Test]
    public async Task TxPoolContentFrom_WhenAddressNotInPool_SerializesEmptyPendingAndQueued()
    {
        ITxPoolInfoProvider txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
        txPoolInfoProvider.GetInfo().Returns(new TxPoolInfo(pending: new(), queued: new()));

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(1ul);
        TxPoolRpcModule txPoolRpcModule = new(txPoolInfoProvider, specProvider);

        string json = await RpcTest.TestSerializedRequest<ITxPoolRpcModule>(txPoolRpcModule, "txpool_contentFrom", TestItem.AddressA);

        json.Should().Contain("\"pending\":{}", "spec requires the pending field to always be present");
        json.Should().Contain("\"queued\":{}", "spec requires the queued field to always be present");
    }
}
