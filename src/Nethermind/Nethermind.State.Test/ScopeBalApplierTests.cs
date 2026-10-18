// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
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
    [TestCase(0, false)]
    [TestCase(15, false)]
    [TestCase(0, true)]
    public void ApplyBal_DisposesEveryStorageBatchWhenWorkerFails(int failingIndex, bool disposalAlsoFails)
    {
        const int count = 16;
        int caller = Environment.CurrentManagedThreadId;
        int active = 0;
        int ownerViolations = 0;
        int activeAtParentDispose = -1;
        int[]? disposedAtParent = null;
        int[] disposed = new int[count];
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
                    if (index == failingIndex) throw new InvalidOperationException("storage write failed");
                }
                finally { Interlocked.Decrement(ref active); }
            });
            child.When(x => x.Dispose()).Do(_ =>
            {
                Interlocked.Increment(ref disposed[index]);
                if (disposalAlsoFails && index == 7) throw new InvalidOperationException("storage dispose failed");
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

        // The largest batch fails first on a single worker, leaving batch 7 for exceptional cleanup.
        using ParallelUnbalancedWork.WorkerScope workerScope = ParallelUnbalancedWork.BeginWorkerScope(disposalAlsoFails ? 1 : Environment.ProcessorCount);
        Exception? failure = Assert.Catch(() => ScopeBalApplier.Apply(scope, bal, LimboLogs.Instance));
        parent.Received(1).Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.ToString(), Does.Contain("storage write failed"));
            if (disposalAlsoFails) Assert.That(failure.ToString(), Does.Contain("storage dispose failed"));
            Assert.That(ownerViolations, Is.Zero);
            Assert.That(activeAtParentDispose, Is.Zero, "all workers must have joined before parent disposal");
            Assert.That(disposedAtParent, Is.All.EqualTo(1), "every staged batch must be disposed before its parent");
            Assert.That(disposed, Is.All.EqualTo(1));
        }
    }
}
