// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture]
public class OverlaidScopeProviderTests
{
    private static readonly Account Underlying = new(1, 10, TestItem.KeccakA, TestItem.KeccakB);
    private static readonly Account Overlaid = new(2, 20, TestItem.KeccakC, TestItem.KeccakD);

    private IWorldStateScopeProvider.IScope _inner = null!;
    private IWorldStateScopeProvider.IStorageTree _innerTree = null!;
    private StateReadOverlaySlot _slot = null!;
    private IWorldStateScopeProvider.IScope _scope = null!;

    [SetUp]
    public void SetUp()
    {
        _inner = Substitute.For<IWorldStateScopeProvider.IScope>();
        _inner.Get(TestItem.AddressA).Returns(Underlying);
        _innerTree = Substitute.For<IWorldStateScopeProvider.IStorageTree>();
        _innerTree.RootHash.Returns(Keccak.EmptyTreeHash);
        _innerTree.When(t => t.Get(Arg.Any<UInt256>(), out Arg.Any<UInt256>())).Do(c => c[1] = (UInt256)99);
        _inner.CreateStorageTree(TestItem.AddressA).Returns(_innerTree);
        IWorldStateScopeProvider provider = Substitute.For<IWorldStateScopeProvider>();
        provider.TryBeginScope(Arg.Any<BlockHeader>(), Arg.Any<LocalMetrics>(), out Arg.Any<IWorldStateScopeProvider.IScope>()).Returns(call => call.Succeed(2, _inner));
        _slot = new StateReadOverlaySlot();
        _scope = new OverlaidScopeProvider(provider, _slot).BeginScope(null, new LocalMetrics());
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        _inner.Dispose();
    }

    [Test]
    public void WithNothingArmed_ReadsPassThrough()
    {
        _scope.CreateStorageTree(TestItem.AddressA).Get(1, out UInt256 slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_scope.Get(TestItem.AddressA), Is.SameAs(Underlying));
            Assert.That(slot, Is.EqualTo((UInt256)99));
            Assert.That(_scope.CreateStorageTree(TestItem.AddressA).RootHash, Is.EqualTo(Keccak.EmptyTreeHash));
        }
    }

    [Test]
    public void AnArmedOverlay_AnswersAheadOfTheScope()
    {
        _slot.Arm(new FixedOverlay(), Substitute.For<IDisposable>());

        Account account = _scope.Get(TestItem.AddressA);
        _scope.CreateStorageTree(TestItem.AddressA).Get(1, out UInt256 known);
        _scope.CreateStorageTree(TestItem.AddressA).Get(2, out UInt256 unknown);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account, Is.SameAs(Overlaid));
            Assert.That(known, Is.EqualTo((UInt256)7));
            Assert.That(unknown, Is.EqualTo((UInt256)99), "a slot the overlay does not hold comes from the scope underneath");
            Assert.That(_scope.CreateStorageTree(TestItem.AddressA).RootHash, Is.Not.EqualTo(Keccak.EmptyTreeHash),
                "an empty root would make the storage provider skip the tree, and the overlay's slots with it");
            Assert.That(_scope.Get(TestItem.AddressB), Is.Null, "an account the overlay does not know falls through");
        }
    }

    [Test]
    public void Disarming_RestoresPassThrough_AndReleasesTheLease()
    {
        IDisposable lease = Substitute.For<IDisposable>();
        _slot.Arm(new FixedOverlay(), lease);

        _slot.Disarm();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_scope.Get(TestItem.AddressA), Is.SameAs(Underlying));
            lease.Received(1).Dispose();
        }
    }

    private sealed class FixedOverlay : IStateReadOverlay
    {
        public bool TryGetAccount(Address address, Account underlying, out Account overlaid)
        {
            overlaid = address == TestItem.AddressA ? Overlaid : null;
            return address == TestItem.AddressA;
        }

        public bool TryGetStorage(Address address, in UInt256 index, out UInt256 value)
        {
            value = 7;
            return address == TestItem.AddressA && index == 1;
        }

        public bool HasStorage(Address address) => address == TestItem.AddressA;
    }

    [Test]
    public void TheStateUnderOneBlocksOverlays_IsReadFromTheScopeOnce([Values(2, 12)] int cacheBits)
    {
        BlockReadCache cache = new(accountSetsBits: cacheBits);
        IStateReadOverlay first = Substitute.For<IStateReadOverlay>();
        IStateReadOverlay second = Substitute.For<IStateReadOverlay>();

        _slot.Arm(first, Substitute.For<IDisposable>(), cache);
        Account one = _scope.Get(TestItem.AddressA);
        _slot.Arm(second, Substitute.For<IDisposable>(), cache);
        Account two = _scope.Get(TestItem.AddressA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(one, Is.EqualTo(Underlying));
            Assert.That(two, Is.EqualTo(Underlying), "the second transaction of the block reads the same state as the first");
            _inner.Received(1).Get(TestItem.AddressA);
        }
    }

    [Test]
    public void ACachedAccount_IsStillOverlaidByTheTransactionsOwnWrites()
    {
        BlockReadCache cache = new();
        IStateReadOverlay plain = Substitute.For<IStateReadOverlay>();
        IStateReadOverlay writing = Substitute.For<IStateReadOverlay>();
        writing.TryGetAccount(TestItem.AddressA, Arg.Any<Account>(), out Arg.Any<Account>())
            .Returns(c => { c[2] = Overlaid; return true; });

        _slot.Arm(plain, Substitute.For<IDisposable>(), cache);
        _scope.Get(TestItem.AddressA);
        _slot.Arm(writing, Substitute.For<IDisposable>(), cache);
        Account overlaid = _scope.Get(TestItem.AddressA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(overlaid, Is.EqualTo(Overlaid), "the cache holds the state under the overlay, not the answer");
            _inner.Received(1).Get(TestItem.AddressA);
        }
    }

    [Test]
    public void AStorageSlot_IsReadFromTheTreeOnceForTheBlock([Values(4, 14)] int cacheBits)
    {
        BlockReadCache cache = new(storageSetsBits: cacheBits);
        IStateReadOverlay overlay = Substitute.For<IStateReadOverlay>();
        IWorldStateScopeProvider.IStorageTree tree = _scope.CreateStorageTree(TestItem.AddressA);

        _slot.Arm(overlay, Substitute.For<IDisposable>(), cache);
        tree.Get(1, out UInt256 first);
        _slot.Arm(Substitute.For<IStateReadOverlay>(), Substitute.For<IDisposable>(), cache);
        tree.Get(1, out UInt256 second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo((UInt256)99));
            Assert.That(second, Is.EqualTo((UInt256)99));
            _innerTree.Received(1).Get(Arg.Any<UInt256>(), out Arg.Any<UInt256>());
        }
    }

    [TestCase(1, 7, TestName = "SlotThePrefixWrote")]
    [TestCase(2, 5, TestName = "SlotOnlyTheParentStateHolds")]
    [TestCase(3, 99, TestName = "SlotTheParentStateDoesNotAnswer")]
    public void AStorageRead_WithAParentState_AnswersPrefixThenParentStateThenScope(int index, int expected)
    {
        ParentStateOverlay parent = new();
        _slot.Arm(new FixedOverlay(), Substitute.For<IDisposable>(), new BlockReadCache(), parent);
        IWorldStateScopeProvider.IStorageTree tree = _scope.CreateStorageTree(TestItem.AddressA);

        tree.Get((UInt256)index, out UInt256 first);
        tree.Get((UInt256)index, out UInt256 second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo((UInt256)expected), "the prefix answers first, then the earlier blocks, then the scope");
            Assert.That(second, Is.EqualTo((UInt256)expected), "a read the cache answers is the read the layers behind it gave");
        }
    }

    [Test]
    public void AStorageRead_RepeatedInTheBlock_AsksTheParentStateOnce()
    {
        ParentStateOverlay parent = new();
        BlockReadCache cache = new();
        IWorldStateScopeProvider.IStorageTree tree = _scope.CreateStorageTree(TestItem.AddressA);

        _slot.Arm(Substitute.For<IStateReadOverlay>(), Substitute.For<IDisposable>(), cache, parent);
        tree.Get(2, out UInt256 first);
        tree.Get(3, out UInt256 untouched);
        _slot.Arm(Substitute.For<IStateReadOverlay>(), Substitute.For<IDisposable>(), cache, parent);
        tree.Get(2, out UInt256 second);
        tree.Get(3, out UInt256 untouchedAgain);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo((UInt256)5));
            Assert.That(second, Is.EqualTo((UInt256)5), "the next transaction of the block reads the cached parent value");
            Assert.That(untouched, Is.EqualTo((UInt256)99));
            Assert.That(untouchedAgain, Is.EqualTo((UInt256)99));
            Assert.That(parent.StorageProbes, Is.EqualTo(2), "one walk of the earlier blocks per slot per block, not one per read");
            _innerTree.Received(1).Get(Arg.Any<UInt256>(), out Arg.Any<UInt256>());
        }
    }

    [Test]
    public void ACachedSlot_NeverHoldsWhatThePrefixWrote()
    {
        ParentStateOverlay parent = new();
        BlockReadCache cache = new();
        IWorldStateScopeProvider.IStorageTree tree = _scope.CreateStorageTree(TestItem.AddressA);

        _slot.Arm(new FixedOverlay(), Substitute.For<IDisposable>(), cache, parent);
        tree.Get(1, out UInt256 written);
        _slot.Arm(Substitute.For<IStateReadOverlay>(), Substitute.For<IDisposable>(), cache, parent);
        tree.Get(1, out UInt256 beforeTheWrite);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo((UInt256)7), "precondition: the prefix that wrote the slot answers it");
            Assert.That(beforeTheWrite, Is.EqualTo((UInt256)6), "a transaction ahead of the write reads the parent value, not the prefix's");
        }
    }

    [Test]
    public void AnAccount_RepeatedInTheBlock_AsksTheParentStateOnce_AndStaysUnderThePrefix()
    {
        ParentStateOverlay parent = new();
        BlockReadCache cache = new();
        IStateReadOverlay writing = Substitute.For<IStateReadOverlay>();
        writing.TryGetAccount(TestItem.AddressA, Arg.Any<Account>(), out Arg.Any<Account>())
            .Returns(c => { c[2] = ((Account)c[1]).WithChangedNonce(9); return true; });

        _slot.Arm(Substitute.For<IStateReadOverlay>(), Substitute.For<IDisposable>(), cache, parent);
        Account first = _scope.Get(TestItem.AddressA);
        _slot.Arm(writing, Substitute.For<IDisposable>(), cache, parent);
        Account second = _scope.Get(TestItem.AddressA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.SameAs(ParentStateOverlay.Account), "the earlier blocks answer ahead of the scope");
            Assert.That(second.Balance, Is.EqualTo(ParentStateOverlay.Account.Balance), "the prefix is laid over the cached parent account");
            Assert.That(second.Nonce, Is.EqualTo(9UL), "the prefix still answers in front of the cache");
            Assert.That(parent.AccountProbes, Is.EqualTo(1), "one walk of the earlier blocks per account per block, not one per read");
            _inner.Received(1).Get(TestItem.AddressA);
        }
    }

    [Test]
    public void AStorageRoot_IsNotEmpty_WhenOnlyTheParentStateHoldsSlots()
    {
        _slot.Arm(Substitute.For<IStateReadOverlay>(), Substitute.For<IDisposable>(), new BlockReadCache(), new ParentStateOverlay());

        Assert.That(_scope.CreateStorageTree(TestItem.AddressA).RootHash, Is.Not.EqualTo(Keccak.EmptyTreeHash),
            "an empty root would make the storage provider skip the tree, and the earlier blocks' slots with it");
    }

    private sealed class ParentStateOverlay : IStateReadOverlay
    {
        public static readonly Account Account = new(3, 30, TestItem.KeccakA, TestItem.KeccakB);

        public int AccountProbes { get; private set; }
        public int StorageProbes { get; private set; }

        public bool TryGetAccount(Address address, Account underlying, out Account overlaid)
        {
            AccountProbes++;
            overlaid = address == TestItem.AddressA ? Account : null;
            return address == TestItem.AddressA;
        }

        public bool TryGetStorage(Address address, in UInt256 index, out UInt256 value)
        {
            StorageProbes++;
            value = index == 1 ? (UInt256)6 : (UInt256)5;
            return address == TestItem.AddressA && index < 3;
        }

        public bool HasStorage(Address address) => address == TestItem.AddressA;
    }
}
