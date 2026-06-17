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
            using VmState<EthereumGasPolicy> vmState = CreateEvmState();
            try
            {
                StorageCell storageCell = new(TestItem.AddressA, 1);
                Assert.That(vmState.AccessTracker.IsCold(TestItem.AddressA), Is.True);
                Assert.That(vmState.AccessTracker.IsCold(storageCell), Is.True);
            }
            finally
            {
                vmState.Env.Dispose();
            }
        }

        [Test]
        public void Can_warm_address_up_twice()
        {
            using VmState<EthereumGasPolicy> vmState = CreateEvmState();
            try
            {
                Address address = TestItem.AddressA;
                vmState.AccessTracker.WarmUp(address);
                vmState.AccessTracker.WarmUp(address);
                Assert.That(vmState.AccessTracker.IsCold(address), Is.False);
            }
            finally
            {
                vmState.Env.Dispose();
            }
        }

        [Test]
        public void Can_warm_up_many()
        {
            using VmState<EthereumGasPolicy> vmState = CreateEvmState();
            try
            {
                for (int i = 0; i < TestItem.Addresses.Length; i++)
                {
                    vmState.AccessTracker.WarmUp(TestItem.Addresses[i]);
                    vmState.AccessTracker.WarmUp(new StorageCell(TestItem.Addresses[i], 1));
                }

                for (int i = 0; i < TestItem.Addresses.Length; i++)
                {
                    Assert.That(vmState.AccessTracker.IsCold(TestItem.Addresses[i]), Is.False);
                    Assert.That(vmState.AccessTracker.IsCold(new StorageCell(TestItem.Addresses[i], 1)), Is.False);
                }
            }
            finally
            {
                vmState.Env.Dispose();
            }
        }

        [Test]
        public void Can_warm_storage_up_twice()
        {
            using VmState<EthereumGasPolicy> vmState = CreateEvmState();
            try
            {
                Address address = TestItem.AddressA;
                StorageCell storageCell = new(address, 1);
                vmState.AccessTracker.WarmUp(storageCell);
                vmState.AccessTracker.WarmUp(storageCell);
                Assert.That(vmState.AccessTracker.IsCold(storageCell), Is.False);
            }
            finally
            {
                vmState.Env.Dispose();
            }
        }

        [Test]
        public void Nothing_to_commit()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                using VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState);
                vmState.CommitToParent(parentVmState);
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Nothing_to_restore()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                using VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState);
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Address_to_commit_keeps_it_warm()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
                {
                    vmState.AccessTracker.WarmUp(TestItem.AddressA);
                    vmState.CommitToParent(parentVmState);
                }

                Assert.That(parentVmState.AccessTracker.IsCold(TestItem.AddressA), Is.False);
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Address_to_restore_keeps_it_cold()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
                {
                    vmState.AccessTracker.WarmUp(TestItem.AddressA);
                }

                Assert.That(parentVmState.AccessTracker.IsCold(TestItem.AddressA), Is.True);
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Storage_to_commit_keeps_it_warm()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                StorageCell storageCell = new(TestItem.AddressA, 1);
                using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
                {
                    vmState.AccessTracker.WarmUp(storageCell);
                    vmState.CommitToParent(parentVmState);
                }

                Assert.That(parentVmState.AccessTracker.IsCold(storageCell), Is.False);
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Storage_to_restore_keeps_it_cold()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                StorageCell storageCell = new(TestItem.AddressA, 1);
                using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
                {
                    vmState.AccessTracker.WarmUp(storageCell);
                }

                Assert.That(parentVmState.AccessTracker.IsCold(storageCell), Is.True);
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Logs_are_committed()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                LogEntry logEntry = new(Address.Zero, Bytes.Empty, []);
                using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
                {
                    vmState.AccessTracker.Logs.Add(logEntry);
                    vmState.CommitToParent(parentVmState);
                }

                Assert.That(parentVmState.AccessTracker.Logs.Contains(logEntry), Is.True);
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Logs_are_restored()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                LogEntry logEntry = new(Address.Zero, Bytes.Empty, []);
                using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
                {
                    vmState.AccessTracker.Logs.Add(logEntry);
                }

                Assert.That(parentVmState.AccessTracker.Logs.Contains(logEntry), Is.False);
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Destroy_list_is_committed()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
                {
                    vmState.AccessTracker.ToBeDestroyed(Address.Zero);
                    vmState.CommitToParent(parentVmState);
                }

                Assert.That(parentVmState.AccessTracker.DestroyList.Contains(Address.Zero), Is.True);
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Destroy_list_is_restored()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
                {
                    vmState.AccessTracker.ToBeDestroyed(Address.Zero);
                }

                Assert.That(parentVmState.AccessTracker.DestroyList.Contains(Address.Zero), Is.False);
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Commit_adds_refunds()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
                {
                    vmState.Refund = 333;
                    vmState.CommitToParent(parentVmState);
                }

                Assert.That(parentVmState.Refund, Is.EqualTo(333));
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Restore_does_not_add_refunds()
        {
            using VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            try
            {
                using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
                {
                    vmState.Refund = 333;
                }

                Assert.That(parentVmState.Refund, Is.EqualTo(0));
            }
            finally
            {
                parentVmState.Env.Dispose();
            }
        }

        [Test]
        public void Can_dispose_without_init()
        {
            using VmState<EthereumGasPolicy> vmState = CreateEvmState();
            vmState.Env.Dispose();
        }

        [Test]
        public void Can_dispose_after_init()
        {
            using VmState<EthereumGasPolicy> vmState = CreateEvmState();
            try
            {
                vmState.InitializeStacks(default, out EvmStack _);
            }
            finally
            {
                vmState.Env.Dispose();
            }
        }

        private static VmState<EthereumGasPolicy> CreateEvmState(VmState<EthereumGasPolicy> parentVmState = null, bool isContinuation = false) =>
            parentVmState is null
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
                    Snapshot.Empty);

        private static ExecutionEnvironment RentExecutionEnvironment() =>
            ExecutionEnvironment.Rent(null, null, null, null, 0, default, default);
    }
}
