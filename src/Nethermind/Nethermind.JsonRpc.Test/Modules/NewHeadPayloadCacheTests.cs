// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.All)]
public class NewHeadPayloadCacheTests
{
    [TestCase(false, TestName = "WithoutTransactions")]
    [TestCase(true, TestName = "WithTransactions")]
    public void Get_ForTheSameHead_BuildsThePayloadOnce(bool includeTransactions)
    {
        ISpecProvider specProvider = MainnetSpecProvider.Instance;
        IBlockForRpcFactory factory = CountingFactory();
        NewHeadPayloadCache payloads = new(specProvider, factory);
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;

        SerializedJson first = payloads.Get(block, includeTransactions);
        SerializedJson second = payloads.Get(block, includeTransactions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Utf8Json, Is.SameAs(first.Utf8Json), "every subscriber of the head must share one serialized payload");
            factory.Received(1).Create(block, includeTransactions, specProvider);
        }
    }

    [Test]
    public void Get_ForANewHeadOrTheOtherOption_BuildsItsOwnPayload()
    {
        ISpecProvider specProvider = MainnetSpecProvider.Instance;
        NewHeadPayloadCache payloads = new(specProvider, CountingFactory());
        Block head = Build.A.Block.WithNumber(1).TestObject;
        Block next = Build.A.Block.WithNumber(2).WithParent(head).TestObject;

        SerializedJson hashes = payloads.Get(head, includeTransactions: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payloads.Get(head, includeTransactions: true).Utf8Json, Is.Not.SameAs(hashes.Utf8Json), "the full-transaction payload is a different payload");
            Assert.That(payloads.Get(next, includeTransactions: false).Utf8Json, Is.Not.SameAs(hashes.Utf8Json), "a new head must not be served the previous head's payload");
        }
    }

    private static IBlockForRpcFactory CountingFactory()
    {
        BlockForRpcFactory inner = new();
        IBlockForRpcFactory factory = Substitute.For<IBlockForRpcFactory>();
        factory.Create(Arg.Any<Block>(), Arg.Any<bool>(), Arg.Any<ISpecProvider>(), Arg.Any<bool>())
            .Returns(call => inner.Create(call.ArgAt<Block>(0), call.ArgAt<bool>(1), call.ArgAt<ISpecProvider>(2), call.ArgAt<bool>(3)));
        return factory;
    }
}
