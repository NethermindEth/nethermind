// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
internal class XdcMulticall3InstallerTests
{
    private static readonly Address Multicall3 = XdcMulticall3Installer.Address;

    private static ReleaseSpec PragueSpec => new()
    {
        IsEip2935Enabled = true,
        Eip2935RingBufferSize = Eip2935Constants.RingBufferSize,
    };

    // The code hash at 0xcA11…CA11 on Ethereum mainnet and on Apothem (eth_getCode at latest, nonce 1).
    [Test]
    public void Runtime_code_is_the_canonical_deployment()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(XdcMulticall3Installer.RuntimeCode, Has.Length.EqualTo(3808));
            Assert.That(Keccak.Compute(XdcMulticall3Installer.RuntimeCode),
                Is.EqualTo(new Hash256("0xd5c15df687b16f2ff992fc8d767b4216323184a2bbc6ee2f9c398c318e770891")));
        }
    }

    [Test]
    public void Installs_before_the_inner_store_and_keeps_the_balance([Values] bool accountExists)
    {
        (IWorldState worldState, IDisposable scope) = CreateWorldState();
        using IDisposable _ = scope;
        ReleaseSpec spec = PragueSpec;
        UInt256 balance = accountExists ? 5UL : 0UL;
        if (accountExists)
        {
            worldState.CreateAccount(Multicall3, balance);
            worldState.Commit(spec);
        }

        IBlockhashStore inner = Substitute.For<IBlockhashStore>();
        bool installedBeforeInner = false;
        inner.When(i => i.ApplyBlockhashStateChanges(Arg.Any<BlockHeader>(), Arg.Any<IReleaseSpec>()))
            .Do(_ => installedBeforeInner = worldState.IsContract(Multicall3));

        new XdcMulticall3Installer(inner, worldState).ApplyBlockhashStateChanges(Header(42), spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(installedBeforeInner, Is.True);
            Assert.That(worldState.GetCode(Multicall3), Is.SequenceEqualTo(XdcMulticall3Installer.RuntimeCode));
            Assert.That(worldState.GetNonce(Multicall3), Is.EqualTo(1UL));
            Assert.That(worldState.GetBalance(Multicall3), Is.EqualTo(balance));
        }
    }

    [TestCase(false, 42UL, TestName = "Does_not_install_before_Prague")]
    [TestCase(true, 0UL, TestName = "Does_not_install_at_genesis")]
    public void Does_not_install(bool prague, ulong blockNumber)
    {
        (IWorldState worldState, IDisposable scope) = CreateWorldState();
        using IDisposable _ = scope;
        ReleaseSpec spec = prague ? PragueSpec : new ReleaseSpec();
        IBlockhashStore inner = Substitute.For<IBlockhashStore>();
        BlockHeader header = Header(blockNumber);

        new XdcMulticall3Installer(inner, worldState).ApplyBlockhashStateChanges(header, spec);

        Assert.That(worldState.AccountExists(Multicall3), Is.False);
        inner.Received(1).ApplyBlockhashStateChanges(header, spec);
    }

    [Test]
    public void Leaves_the_canonical_code_alone()
    {
        (IWorldState worldState, IDisposable scope) = CreateWorldState();
        using IDisposable _ = scope;
        ReleaseSpec spec = PragueSpec;
        const ulong existingNonce = 7;
        Deploy(worldState, spec, XdcMulticall3Installer.RuntimeCode, existingNonce);

        new XdcMulticall3Installer(Substitute.For<IBlockhashStore>(), worldState).ApplyBlockhashStateChanges(Header(42), spec);

        Assert.That(worldState.GetNonce(Multicall3), Is.EqualTo(existingNonce));
    }

    [Test]
    public void Throws_on_mismatched_code()
    {
        (IWorldState worldState, IDisposable scope) = CreateWorldState();
        using IDisposable _ = scope;
        ReleaseSpec spec = PragueSpec;
        Deploy(worldState, spec, [1, 2, 3], 1);
        IBlockhashStore inner = Substitute.For<IBlockhashStore>();

        Assert.Throws<InvalidOperationException>(() =>
            new XdcMulticall3Installer(inner, worldState).ApplyBlockhashStateChanges(Header(42), spec));
        inner.DidNotReceiveWithAnyArgs().ApplyBlockhashStateChanges(default!, default!);
    }

    // Dropping IHasAccessList from the decorator silently disables the EIP-2935 store's prewarm hint.
    [Test]
    public void GetAccessList_forwards_the_inner_hint()
    {
        (IWorldState worldState, IDisposable scope) = CreateWorldState();
        using IDisposable _ = scope;
        ReleaseSpec spec = PragueSpec;
        Block block = Build.A.Block.WithNumber(42).WithParentHash(TestItem.KeccakA).TestObject;

        XdcMulticall3Installer store = new(new XdcBlockhashStore(new BlockhashStore(worldState), worldState), worldState);
        store.ApplyBlockhashStateChanges(block.Header, spec);

        AccessList? hint = store.GetAccessList(block, spec);

        Assert.That(hint, Is.Not.Null);
        foreach ((Address address, AccessList.StorageKeysEnumerable _) in hint!)
        {
            Assert.That(address, Is.EqualTo(Eip2935Constants.BlockHashHistoryAddress));
        }
    }

    private static (IWorldState, IDisposable) CreateWorldState()
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        return (worldState, worldState.BeginScope(IWorldState.PreGenesis));
    }

    private static BlockHeader Header(ulong number) =>
        Build.A.BlockHeader.WithNumber(number).WithParentHash(TestItem.KeccakA).TestObject;

    private static void Deploy(IWorldState worldState, IReleaseSpec spec, byte[] code, ulong nonce)
    {
        worldState.CreateAccount(Multicall3, 0, nonce);
        worldState.InsertCode(Multicall3, ValueKeccak.Compute(code), code, spec);
        worldState.Commit(spec);
    }
}
