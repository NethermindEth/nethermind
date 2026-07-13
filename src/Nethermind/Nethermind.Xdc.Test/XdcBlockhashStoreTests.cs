// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

internal class XdcBlockhashStoreTests
{
    private static readonly Address Eip2935Account = Eip2935Constants.BlockHashHistoryAddress;

    private static ReleaseSpec Eip2935Spec => new()
    {
        IsEip2935Enabled = true,
        Eip2935RingBufferSize = Eip2935Constants.RingBufferSize,
    };

    // Dropping IHasAccessList from the decorator silently disables the inner store's prewarm hint.
    [TestCase(true, TestName = "GetAccessList_WithHintCapableInner_ForwardsTheInnerHint")]
    [TestCase(false, TestName = "GetAccessList_WithHintlessInner_IsNull")]
    public void GetAccessList_AtGivenInnerCapability_ForwardsExactly(bool innerHintCapable)
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        ReleaseSpec spec = Eip2935Spec;
        Block block = Build.A.Block.WithNumber(42).WithParentHash(TestItem.KeccakA).TestObject;
        IBlockhashStore inner = innerHintCapable ? new BlockhashStore(worldState) : Substitute.For<IBlockhashStore>();
        XdcBlockhashStore store = new(inner, worldState);
        store.ApplyBlockhashStateChanges(block.Header, spec);

        AccessList? hint = store.GetAccessList(block, spec);

        if (!innerHintCapable)
        {
            Assert.That(hint, Is.Null);
            return;
        }

        Assert.That(hint, Is.Not.Null, "the decorator must forward the inner store's hint");
        foreach ((Address address, AccessList.StorageKeysEnumerable _) in hint!)
        {
            Assert.That(address, Is.EqualTo(Eip2935Account));
        }
    }

    [Test]
    public void Deploys_history_contract_when_missing()
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        ReleaseSpec spec = Eip2935Spec;
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).WithParentHash(TestItem.KeccakA).TestObject;

        XdcBlockhashStore store = new(new BlockhashStore(worldState), worldState);
        store.ApplyBlockhashStateChanges(header, spec);

        Assert.Multiple(() =>
        {
            Assert.That(worldState.IsContract(Eip2935Account), Is.True);
            Assert.That(worldState.GetCode(Eip2935Account), Is.EqualTo(Eip2935Constants.Code));
            Assert.That(worldState.GetNonce(Eip2935Account), Is.EqualTo(1UL));
        });
    }

    [Test]
    public void Stores_parent_hash_after_deploying_contract()
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        ReleaseSpec spec = Eip2935Spec;
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).WithParentHash(TestItem.KeccakA).TestObject;

        XdcBlockhashStore store = new(new BlockhashStore(worldState), worldState);
        store.ApplyBlockhashStateChanges(header, spec);

        Hash256? stored = store.GetBlockHashFromState(header, header.Number - 1, spec);
        Assert.That(stored, Is.EqualTo(header.ParentHash));
    }

    [Test]
    public void Does_not_redeploy_when_history_contract_already_present()
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        ReleaseSpec spec = Eip2935Spec;

        const ulong existingNonce = 7;
        worldState.CreateAccount(Eip2935Account, 0, existingNonce);
        worldState.InsertCode(Eip2935Account, ValueKeccak.Compute(Eip2935Constants.Code), Eip2935Constants.Code, spec);
        worldState.Commit(spec);

        BlockHeader header = Build.A.BlockHeader.WithNumber(42).WithParentHash(TestItem.KeccakA).TestObject;
        XdcBlockhashStore store = new(new BlockhashStore(worldState), worldState);
        store.ApplyBlockhashStateChanges(header, spec);

        Assert.Multiple(() =>
        {
            // Nonce is left untouched — the contract is not redeployed.
            Assert.That(worldState.GetNonce(Eip2935Account), Is.EqualTo(existingNonce));
            Assert.That(store.GetBlockHashFromState(header, header.Number - 1, spec), Is.EqualTo(header.ParentHash));
        });
    }

    [Test]
    public void Throws_when_history_contract_code_mismatches()
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        ReleaseSpec spec = Eip2935Spec;

        byte[] wrongCode = [1, 2, 3];
        worldState.CreateAccount(Eip2935Account, 0, 1);
        worldState.InsertCode(Eip2935Account, ValueKeccak.Compute(wrongCode), wrongCode, spec);
        worldState.Commit(spec);

        BlockHeader header = Build.A.BlockHeader.WithNumber(42).WithParentHash(TestItem.KeccakA).TestObject;
        XdcBlockhashStore store = new(new BlockhashStore(worldState), worldState);

        Assert.Throws<InvalidOperationException>(() => store.ApplyBlockhashStateChanges(header, spec));
    }

    [Test]
    public void Does_nothing_for_genesis_block()
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        ReleaseSpec spec = Eip2935Spec;
        BlockHeader genesis = Build.A.BlockHeader.WithNumber(0).TestObject;

        XdcBlockhashStore store = new(new BlockhashStore(worldState), worldState);
        store.ApplyBlockhashStateChanges(genesis, spec);

        Assert.That(worldState.AccountExists(Eip2935Account), Is.False);
    }

    [Test]
    public void Does_nothing_when_eip2935_disabled()
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        ReleaseSpec spec = new() { IsEip2935Enabled = false };
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).WithParentHash(TestItem.KeccakA).TestObject;

        XdcBlockhashStore store = new(new BlockhashStore(worldState), worldState);
        store.ApplyBlockhashStateChanges(header, spec);

        Assert.That(worldState.AccountExists(Eip2935Account), Is.False);
    }

    [Test]
    public void Does_nothing_when_parent_hash_missing()
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        ReleaseSpec spec = Eip2935Spec;
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).TestObject;
        header.ParentHash = null;

        XdcBlockhashStore store = new(new BlockhashStore(worldState), worldState);
        store.ApplyBlockhashStateChanges(header, spec);

        Assert.That(worldState.AccountExists(Eip2935Account), Is.False);
    }
}
