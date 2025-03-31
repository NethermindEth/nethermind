// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// using System.Collections.Generic;

using System.Collections.Generic;
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
    public void Pool_content_produces_transactions_with_ChainId()
    {
        const ulong SomeChainId = 123ul;
        var txA = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithChainId(null)
            .TestObject;
        var txB = Build.A.Transaction
            .WithType(TxType.AccessList)
            .WithAccessList(AccessList.Empty)
            .WithChainId(null)
            .TestObject;

        var txPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
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

        var specProvider = Substitute.For<ISpecProvider>();
        specProvider.ChainId.Returns(SomeChainId);

        var txPoolRpcModule = new TxPoolRpcModule(txPoolInfoProvider, specProvider);

        var txpoolContent = txPoolRpcModule.txpool_content().Data;

        var rpcTxA = txpoolContent.Pending[new AddressAsKey(TestItem.AddressA)][1] as LegacyTransactionForRpc;
        var rpcTxB = txpoolContent.Queued[new AddressAsKey(TestItem.AddressB)][2] as AccessListTransactionForRpc;

        rpcTxA!.ChainId.Should().BeNull();
        rpcTxB!.ChainId.Should().Be(SomeChainId);
    }
}
