// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture]
public class PrewarmerScopeProviderTests
{
    [Test]
    public void Scope_Get_CallsWarmUpOutOfScope_OnOuterProvider()
    {
        IWorldStateScopeProvider baseProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider outerScopeProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider.IScope baseScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        baseProvider.BeginScope(Arg.Any<BlockHeader?>()).Returns(baseScope);
        baseScope.Get(Arg.Any<Address>()).Returns((Account?)null);

        PreBlockCaches preBlockCaches = new PreBlockCaches();
        PrewarmerScopeProvider provider = new PrewarmerScopeProvider(baseProvider, outerScopeProvider, preBlockCaches, populatePreBlockCache: true);

        using var scope = provider.BeginScope(null);
        scope.Get(TestItem.AddressA);

        outerScopeProvider.Received(1).WarmUpOutOfScope(TestItem.AddressA, null, false);
    }

    [Test]
    public void StorageTree_Get_CallsWarmUpOutOfScope_OnOuterProvider()
    {
        IWorldStateScopeProvider baseProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider outerScopeProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider.IScope baseScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        IWorldStateScopeProvider.IStorageTree baseStorageTree = Substitute.For<IWorldStateScopeProvider.IStorageTree>();
        baseProvider.BeginScope(Arg.Any<BlockHeader?>()).Returns(baseScope);
        baseScope.CreateStorageTree(Arg.Any<Address>()).Returns(baseStorageTree);
        baseStorageTree.Get(Arg.Any<UInt256>()).Returns([]);

        PreBlockCaches preBlockCaches = new PreBlockCaches();
        PrewarmerScopeProvider provider = new PrewarmerScopeProvider(baseProvider, outerScopeProvider, preBlockCaches, populatePreBlockCache: true);

        using var scope = provider.BeginScope(null);
        var storageTree = scope.CreateStorageTree(TestItem.AddressA);
        UInt256 index = 42;
        storageTree.Get(index);

        outerScopeProvider.Received(1).WarmUpOutOfScope(TestItem.AddressA, index, false);
    }

    [Test]
    public void Scope_HintSet_CallsWarmUpOutOfScope_OnOuterProvider()
    {
        IWorldStateScopeProvider baseProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider outerScopeProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider.IScope baseScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        baseProvider.BeginScope(Arg.Any<BlockHeader?>()).Returns(baseScope);

        PreBlockCaches preBlockCaches = new PreBlockCaches();
        PrewarmerScopeProvider provider = new PrewarmerScopeProvider(baseProvider, outerScopeProvider, preBlockCaches, populatePreBlockCache: true);

        using var scope = provider.BeginScope(null);
        scope.HintSet(TestItem.AddressB);

        outerScopeProvider.Received(1).WarmUpOutOfScope(TestItem.AddressB, null, true);
    }

    [Test]
    public void StorageTree_HintSet_CallsWarmUpOutOfScope_OnOuterProvider()
    {
        IWorldStateScopeProvider baseProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider outerScopeProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider.IScope baseScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        IWorldStateScopeProvider.IStorageTree baseStorageTree = Substitute.For<IWorldStateScopeProvider.IStorageTree>();
        baseProvider.BeginScope(Arg.Any<BlockHeader?>()).Returns(baseScope);
        baseScope.CreateStorageTree(Arg.Any<Address>()).Returns(baseStorageTree);

        PreBlockCaches preBlockCaches = new PreBlockCaches();
        PrewarmerScopeProvider provider = new PrewarmerScopeProvider(baseProvider, outerScopeProvider, preBlockCaches, populatePreBlockCache: true);

        using var scope = provider.BeginScope(null);
        var storageTree = scope.CreateStorageTree(TestItem.AddressA);
        UInt256 index = 99;
        storageTree.HintSet(index);

        outerScopeProvider.Received(1).WarmUpOutOfScope(TestItem.AddressA, index, true);
    }

    [Test]
    public void Scope_Get_DoesNotCallWarmUpOutOfScope_WhenNoOuterProvider()
    {
        IWorldStateScopeProvider baseProvider = Substitute.For<IWorldStateScopeProvider>();
        IWorldStateScopeProvider.IScope baseScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        baseProvider.BeginScope(Arg.Any<BlockHeader?>()).Returns(baseScope);
        baseScope.Get(Arg.Any<Address>()).Returns((Account?)null);

        PreBlockCaches preBlockCaches = new PreBlockCaches();
        PrewarmerScopeProvider provider = new PrewarmerScopeProvider(baseProvider, outerScopeProvider: null, preBlockCaches, populatePreBlockCache: true);

        using var scope = provider.BeginScope(null);
        scope.Get(TestItem.AddressA);

        // Should not throw - null check in the code handles this
    }
}
