// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class VmStateTests
    {
        [Test]
        public void Things_are_cold_to_start_with()
        {
            using VmStateScope scope = CreateEvmStateScope();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            Assert.That(scope.VmState.AccessTracker.IsCold(TestItem.AddressA), Is.True);
            Assert.That(scope.VmState.AccessTracker.IsCold(storageCell), Is.True);
        }

        [Test]
        public void Can_warm_address_up_twice()
        {
            using VmStateScope scope = CreateEvmStateScope();
            Address address = TestItem.AddressA;
            scope.VmState.AccessTracker.WarmUp(address);
            scope.VmState.AccessTracker.WarmUp(address);
            Assert.That(scope.VmState.AccessTracker.IsCold(address), Is.False);
        }

        [Test]
        public void Can_warm_up_many()
        {
            using VmStateScope scope = CreateEvmStateScope();
            for (int i = 0; i < TestItem.Addresses.Length; i++)
            {
                scope.VmState.AccessTracker.WarmUp(TestItem.Addresses[i]);
                scope.VmState.AccessTracker.WarmUp(new StorageCell(TestItem.Addresses[i], 1));
            }

            for (int i = 0; i < TestItem.Addresses.Length; i++)
            {
                Assert.That(scope.VmState.AccessTracker.IsCold(TestItem.Addresses[i]), Is.False);
                Assert.That(scope.VmState.AccessTracker.IsCold(new StorageCell(TestItem.Addresses[i], 1)), Is.False);
            }
        }

        [Test]
        public void Can_warm_storage_up_twice()
        {
            using VmStateScope scope = CreateEvmStateScope();
            Address address = TestItem.AddressA;
            StorageCell storageCell = new(address, 1);
            scope.VmState.AccessTracker.WarmUp(storageCell);
            scope.VmState.AccessTracker.WarmUp(storageCell);
            Assert.That(scope.VmState.AccessTracker.IsCold(storageCell), Is.False);
        }

        [Test]
        public void Nothing_to_commit()
        {
            using VmStateScope parent = CreateEvmStateScope();
            using VmStateScope child = CreateEvmStateScope(parent.VmState);
            child.VmState.CommitToParent(parent.VmState);
        }

        [Test]
        public void Nothing_to_restore()
        {
            using VmStateScope parent = CreateEvmStateScope();
            using VmStateScope _ = CreateEvmStateScope(parent.VmState);
        }

        [Test]
        public void Address_to_commit_keeps_it_warm()
        {
            using VmStateScope parent = CreateEvmStateScope();
            using (VmStateScope child = CreateEvmStateScope(parent.VmState))
            {
                child.VmState.AccessTracker.WarmUp(TestItem.AddressA);
                child.VmState.CommitToParent(parent.VmState);
            }

            Assert.That(parent.VmState.AccessTracker.IsCold(TestItem.AddressA), Is.False);
        }

        [Test]
        public void Address_to_restore_keeps_it_cold()
        {
            using VmStateScope parent = CreateEvmStateScope();
            using (VmStateScope child = CreateEvmStateScope(parent.VmState))
            {
                child.VmState.AccessTracker.WarmUp(TestItem.AddressA);
            }

            Assert.That(parent.VmState.AccessTracker.IsCold(TestItem.AddressA), Is.True);
        }

        [Test]
        public void Storage_to_commit_keeps_it_warm()
        {
            using VmStateScope parent = CreateEvmStateScope();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            using (VmStateScope child = CreateEvmStateScope(parent.VmState))
            {
                child.VmState.AccessTracker.WarmUp(storageCell);
                child.VmState.CommitToParent(parent.VmState);
            }

            Assert.That(parent.VmState.AccessTracker.IsCold(storageCell), Is.False);
        }

        [Test]
        public void Storage_to_restore_keeps_it_cold()
        {
            using VmStateScope parent = CreateEvmStateScope();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            using (VmStateScope child = CreateEvmStateScope(parent.VmState))
            {
                child.VmState.AccessTracker.WarmUp(storageCell);
            }

            Assert.That(parent.VmState.AccessTracker.IsCold(storageCell), Is.True);
        }

        [Test]
        public void Logs_are_committed()
        {
            using VmStateScope parent = CreateEvmStateScope();
            LogEntry logEntry = new(Address.Zero, Bytes.Empty, []);
            using (VmStateScope child = CreateEvmStateScope(parent.VmState))
            {
                child.VmState.AccessTracker.Logs.Add(logEntry);
                child.VmState.CommitToParent(parent.VmState);
            }

            Assert.That(parent.VmState.AccessTracker.Logs.Contains(logEntry), Is.True);
        }

        [Test]
        public void Logs_are_restored()
        {
            using VmStateScope parent = CreateEvmStateScope();
            LogEntry logEntry = new(Address.Zero, Bytes.Empty, []);
            using (VmStateScope child = CreateEvmStateScope(parent.VmState))
            {
                child.VmState.AccessTracker.Logs.Add(logEntry);
            }

            Assert.That(parent.VmState.AccessTracker.Logs.Contains(logEntry), Is.False);
        }

        [Test]
        public void Destroy_list_is_committed()
        {
            using VmStateScope parent = CreateEvmStateScope();
            using (VmStateScope child = CreateEvmStateScope(parent.VmState))
            {
                child.VmState.AccessTracker.ToBeDestroyed(Address.Zero);
                child.VmState.CommitToParent(parent.VmState);
            }

            Assert.That(parent.VmState.AccessTracker.DestroyList.Contains(Address.Zero), Is.True);
        }

        [Test]
        public void Destroy_list_is_restored()
        {
            using VmStateScope parent = CreateEvmStateScope();
            using (VmStateScope child = CreateEvmStateScope(parent.VmState))
            {
                child.VmState.AccessTracker.ToBeDestroyed(Address.Zero);
            }

            Assert.That(parent.VmState.AccessTracker.DestroyList.Contains(Address.Zero), Is.False);
        }

        [Test]
        public void Commit_adds_refunds()
        {
            using VmStateScope parent = CreateEvmStateScope();
            using (VmStateScope child = CreateEvmStateScope(parent.VmState))
            {
                child.VmState.Refund = 333;
                child.VmState.CommitToParent(parent.VmState);
            }

            Assert.That(parent.VmState.Refund, Is.EqualTo(333));
        }

        [Test]
        public void Restore_does_not_add_refunds()
        {
            using VmStateScope parent = CreateEvmStateScope();
            using (VmStateScope child = CreateEvmStateScope(parent.VmState))
            {
                child.VmState.Refund = 333;
            }

            Assert.That(parent.VmState.Refund, Is.EqualTo(0));
        }

        [Test]
        public void Can_dispose_without_init()
        {
            using VmStateScope scope = CreateEvmStateScope();
        }

        [Test]
        public void Can_dispose_after_init()
        {
            using VmStateScope scope = CreateEvmStateScope();
            scope.VmState.InitializeStacks(default, out EvmStack _);
        }

        private static VmStateScope CreateEvmStateScope(VmState<EthereumGasPolicy> parentVmState = null) =>
            new(parentVmState is null
                ? VmState<EthereumGasPolicy>.RentTopLevel(EthereumGasPolicy.FromULong(10000),
                    ExecutionType.CALL,
                    RentExecutionEnvironment(),
                    new StackAccessTracker(),
                    Snapshot.Empty)
                : VmState<EthereumGasPolicy>.RentFrame(EthereumGasPolicy.FromULong(10000),
                    0,
                    0,
                    ExecutionType.CALL,
                    false,
                    false,
                    RentExecutionEnvironment(),
                    parentVmState.AccessTracker,
                    Snapshot.Empty));

        private static ExecutionEnvironment RentExecutionEnvironment() =>
            ExecutionEnvironment.Rent(null, null, null, null, 0, default, default);

        // VmState.Dispose only releases its ExecutionEnvironment for non-top-level frames; the
        // top-level Env is caller-owned and otherwise leaks. Bundle both so tests dispose cleanly.
        private readonly struct VmStateScope(VmState<EthereumGasPolicy> vmState) : System.IDisposable
        {
            public VmState<EthereumGasPolicy> VmState { get; } = vmState;

            public void Dispose()
            {
                ExecutionEnvironment env = VmState.Env;
                VmState.Dispose();
                env.Dispose();
            }
        }
    }
}
