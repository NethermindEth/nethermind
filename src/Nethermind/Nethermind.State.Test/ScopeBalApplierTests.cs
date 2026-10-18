// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class ScopeBalApplierTests
{
    [TestCase(0, -1)]
    [TestCase(15, -1)]
    [TestCase(0, 7)]
    [TestCase(0, 0)]
    [TestCase(-1, 7)]
    public void ApplyBal_DisposesEveryStorageBatchWhenStorageBatchFails(int failingIndex, int failingDisposeIndex)
    {
        const int count = 16;
        bool disposalAlsoFails = failingDisposeIndex >= 0;
        int caller = Environment.CurrentManagedThreadId;
        int active = 0;
        int ownerViolations = 0;
        int activeAtParentDispose = -1;
        int[]? disposedAtParent = null;
        int[] disposed = new int[count];
        InvalidOperationException writeFailure = new("storage write failed");
        InvalidOperationException disposeFailure = new("storage dispose failed");
        IWorldStateScopeProvider.IScope scope = Substitute.For<IWorldStateScopeProvider.IScope>();
        IWorldStateScopeProvider.IWorldStateWriteBatch parent = Substitute.For<IWorldStateScopeProvider.IWorldStateWriteBatch>();
        scope.StartWriteBatch(count).Returns(parent);
        scope.Get(Arg.Any<Address>()).Returns(new Account(nonce: 1, balance: 1));
        ReadOnlyAccountChanges[] changes = new ReadOnlyAccountChanges[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            Address address = Address.FromNumber((UInt256)(i + 1));
            AccountChangesBuilder accountChanges = Build.An.AccountChanges.WithAddress(address)
                .WithNonceChanges(new NonceChange(1, 2))
                .WithStorageChanges(1, new StorageChange(1, 2));
            if (disposalAlsoFails && index == failingIndex) accountChanges.WithStorageChanges(2, new StorageChange(1, 2));
            changes[i] = accountChanges.TestObject;
            IWorldStateScopeProvider.IStorageWriteBatch child = Substitute.For<IWorldStateScopeProvider.IStorageWriteBatch>();
            parent.CreateStorageWriteBatch(address, changes[i].StorageChanges.Length).Returns(_ =>
            {
                if (Environment.CurrentManagedThreadId != caller) Interlocked.Increment(ref ownerViolations);
                return child;
            });
            child.When(x => x.Set(Arg.Any<UInt256>(), Arg.Any<UInt256>())).Do(_ =>
            {
                Interlocked.Increment(ref active);
                try
                {
                    Thread.Yield();
                    if (index == failingIndex) throw writeFailure;
                }
                finally { Interlocked.Decrement(ref active); }
            });
            child.When(x => x.Dispose()).Do(_ =>
            {
                Interlocked.Increment(ref disposed[index]);
                if (index == failingDisposeIndex) throw disposeFailure;
            });
        }
        parent.When(x => x.Set(Arg.Any<Address>(), Arg.Any<Account?>())).Do(_ =>
        {
            if (Environment.CurrentManagedThreadId != caller) Interlocked.Increment(ref ownerViolations);
        });
        parent.When(x => x.Dispose()).Do(_ =>
        {
            activeAtParentDispose = Volatile.Read(ref active);
            disposedAtParent = (int[])disposed.Clone();
        });
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(changes).TestObject;

        // The largest batch fails first on a single worker, leaving it and the unclaimed batches for exceptional cleanup.
        using ParallelUnbalancedWork.WorkerScope workerScope = ParallelUnbalancedWork.BeginWorkerScope(disposalAlsoFails ? 1 : Environment.ProcessorCount);
        Exception? failure = Assert.Catch(() => ScopeBalApplier.Apply(scope, bal, LimboLogs.Instance));
        parent.Received(1).Dispose();
        using (Assert.EnterMultipleScope())
        {
            // A secondary disposal failure must not replace or wrap the original, so callers can still catch its type;
            // a dispose failing after its writes succeeded is itself the original.
            Assert.That(failure, Is.SameAs(failingIndex >= 0 ? writeFailure : disposeFailure));
            Assert.That(ownerViolations, Is.Zero);
            Assert.That(activeAtParentDispose, Is.Zero, "all workers must have joined before parent disposal");
            Assert.That(disposedAtParent, Is.All.EqualTo(1), "every staged batch must be disposed before its parent");
            Assert.That(disposed, Is.All.EqualTo(1));
        }
    }

    [TestCase(true, false)]
    [TestCase(true, true)]
    [TestCase(false, true)]
    public void ApplyBal_KeepsOriginalFailureWhenParentDisposalAlsoFails(bool writeFails, bool codeSetterDisposalFails)
    {
        InvalidOperationException writeFailure = new("storage write failed");
        InvalidOperationException parentDisposeFailure = new("parent dispose failed");
        IWorldStateScopeProvider.IScope scope = Substitute.For<IWorldStateScopeProvider.IScope>();
        IWorldStateScopeProvider.IWorldStateWriteBatch parent = Substitute.For<IWorldStateScopeProvider.IWorldStateWriteBatch>();
        IWorldStateScopeProvider.IStorageWriteBatch child = Substitute.For<IWorldStateScopeProvider.IStorageWriteBatch>();
        // A hand-written fake: a proxy cannot take the span Set receives.
        CodeSetter codeSetter = new(codeSetterDisposalFails ? new InvalidOperationException("code setter dispose failed") : null);
        scope.StartWriteBatch(Arg.Any<int>()).Returns(parent);
        scope.Get(Arg.Any<Address>()).Returns(new Account(nonce: 1, balance: 1));
        scope.CodeDb.BeginCodeWrite().Returns(codeSetter);
        parent.CreateStorageWriteBatch(Arg.Any<Address>(), Arg.Any<int>()).Returns(child);
        if (writeFails) child.When(x => x.Set(Arg.Any<UInt256>(), Arg.Any<UInt256>())).Do(_ => throw writeFailure);
        parent.When(x => x.Dispose()).Do(_ => throw parentDisposeFailure);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            Build.An.AccountChanges.WithAddress(TestItem.AddressA)
                .WithNonceChanges(new NonceChange(1, 2))
                .WithCodeChanges(new CodeChange(1, [0x00]))
                .WithStorageChanges(1, new StorageChange(1, 2)).TestObject).TestObject;

        Exception? failure = Assert.Catch(() => ScopeBalApplier.Apply(scope, bal, LimboLogs.Instance));
        // Without an earlier failure, the parent's disposal is itself the original and must not be swallowed.
        Assert.That(failure, Is.SameAs(writeFails ? writeFailure : parentDisposeFailure));
        child.Received(1).Dispose();
        parent.Received(1).Dispose();
        Assert.That(codeSetter.Disposed, Is.EqualTo(1));
    }

    private sealed class CodeSetter(Exception? disposeFailure) : IWorldStateScopeProvider.ICodeSetter
    {
        public int Disposed { get; private set; }

        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) { }

        public void Dispose()
        {
            Disposed++;
            if (disposeFailure is not null) throw disposeFailure;
        }
    }
}
