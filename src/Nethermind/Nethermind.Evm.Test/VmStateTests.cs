// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Gas;
using Nethermind.Evm.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class VmStateTests
    {
        [Test]
        public void Things_are_cold_to_start_with()
        {
            VmState<EthereumGasPolicy> vmState = CreateEvmState();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            vmState.AccessTracker.IsCold(TestItem.AddressA).Should().BeTrue();
            vmState.AccessTracker.IsCold(storageCell).Should().BeTrue();
        }

        [Test]
        public void Can_warm_address_up_twice()
        {
            VmState<EthereumGasPolicy> vmState = CreateEvmState();
            Address address = TestItem.AddressA;
            vmState.AccessTracker.WarmUp(address);
            vmState.AccessTracker.WarmUp(address);
            vmState.AccessTracker.IsCold(address).Should().BeFalse();
        }

        [Test]
        public void Can_warm_up_many()
        {
            VmState<EthereumGasPolicy> vmState = CreateEvmState();
            for (int i = 0; i < TestItem.Addresses.Length; i++)
            {
                vmState.AccessTracker.WarmUp(TestItem.Addresses[i]);
                vmState.AccessTracker.WarmUp(new StorageCell(TestItem.Addresses[i], 1));
            }

            for (int i = 0; i < TestItem.Addresses.Length; i++)
            {
                vmState.AccessTracker.IsCold(TestItem.Addresses[i]).Should().BeFalse();
                vmState.AccessTracker.IsCold(new StorageCell(TestItem.Addresses[i], 1)).Should().BeFalse();
            }
        }

        [Test]
        public void Can_warm_storage_up_twice()
        {
            VmState<EthereumGasPolicy> vmState = CreateEvmState();
            Address address = TestItem.AddressA;
            StorageCell storageCell = new(address, 1);
            vmState.AccessTracker.WarmUp(storageCell);
            vmState.AccessTracker.WarmUp(storageCell);
            vmState.AccessTracker.IsCold(storageCell).Should().BeFalse();
        }

        [Test]
        public void Nothing_to_commit()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
            {
                vmState.CommitToParent(parentVmState);
            }
        }

        [Test]
        public void Nothing_to_restore()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            using VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState);
        }

        [Test]
        public void Address_to_commit_keeps_it_warm()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
            {
                vmState.AccessTracker.WarmUp(TestItem.AddressA);
                vmState.CommitToParent(parentVmState);
            }

            parentVmState.AccessTracker.IsCold(TestItem.AddressA).Should().BeFalse();
        }

        [Test]
        public void Address_to_restore_keeps_it_cold()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
            {
                vmState.AccessTracker.WarmUp(TestItem.AddressA);
            }

            parentVmState.AccessTracker.IsCold(TestItem.AddressA).Should().BeTrue();
        }

        [Test]
        public void Storage_to_commit_keeps_it_warm()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
            {
                vmState.AccessTracker.WarmUp(storageCell);
                vmState.CommitToParent(parentVmState);
            }

            parentVmState.AccessTracker.IsCold(storageCell).Should().BeFalse();
        }

        [Test]
        public void Storage_to_restore_keeps_it_cold()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
            {
                vmState.AccessTracker.WarmUp(storageCell);
            }

            parentVmState.AccessTracker.IsCold(storageCell).Should().BeTrue();
        }

        [Test]
        public void Logs_are_committed()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            LogEntry logEntry = new(Address.Zero, Bytes.Empty, []);
            using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
            {
                vmState.AccessTracker.Logs.Add(logEntry);
                vmState.CommitToParent(parentVmState);
            }

            parentVmState.AccessTracker.Logs.Contains(logEntry).Should().BeTrue();
        }

        [Test]
        public void Logs_are_restored()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            LogEntry logEntry = new(Address.Zero, Bytes.Empty, []);
            using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
            {
                vmState.AccessTracker.Logs.Add(logEntry);
            }

            parentVmState.AccessTracker.Logs.Contains(logEntry).Should().BeFalse();
        }

        [Test]
        public void Destroy_list_is_committed()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
            {
                vmState.AccessTracker.ToBeDestroyed(Address.Zero);
                vmState.CommitToParent(parentVmState);
            }

            parentVmState.AccessTracker.DestroyList.Contains(Address.Zero).Should().BeTrue();
        }

        [Test]
        public void Destroy_list_is_restored()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
            {
                vmState.AccessTracker.ToBeDestroyed(Address.Zero);
            }

            parentVmState.AccessTracker.DestroyList.Contains(Address.Zero).Should().BeFalse();
        }

        [Test]
        public void Commit_adds_refunds()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
            {
                vmState.Refund = 333;
                vmState.CommitToParent(parentVmState);
            }

            parentVmState.Refund.Should().Be(333);
        }

        [Test]
        public void Restore_does_not_add_refunds()
        {
            VmState<EthereumGasPolicy> parentVmState = CreateEvmState();
            using (VmState<EthereumGasPolicy> vmState = CreateEvmState(parentVmState))
            {
                vmState.Refund = 333;
            }

            parentVmState.Refund.Should().Be(0);
        }

        [Test]
        public void Can_dispose_without_init()
        {
            VmState<EthereumGasPolicy> vmState = CreateEvmState();
            vmState.Dispose();
        }

        [Test]
        public void Can_dispose_after_init()
        {
            VmState<EthereumGasPolicy> vmState = CreateEvmState();
            vmState.InitializeStacks();
            vmState.Dispose();
        }

        private static VmState<EthereumGasPolicy> CreateEvmState(VmState<EthereumGasPolicy> parentVmState = null, bool isContinuation = false) =>
            parentVmState is null
                ? VmState<EthereumGasPolicy>.RentTopLevel(EthereumGasPolicy.FromLong(10000),
                    ExecutionType.CALL,
                    RentExecutionEnvironment(),
                    new StackAccessTracker(),
                    Snapshot.Empty)
                : VmState<EthereumGasPolicy>.RentFrame(EthereumGasPolicy.FromLong(10000),
                    0,
                    0,
                    ExecutionType.CALL,
                    false,
                    false,
                    RentExecutionEnvironment(),
                    parentVmState.AccessTracker,
                    Snapshot.Empty);

        private static ExecutionEnvironment RentExecutionEnvironment() =>
            ExecutionEnvironment.Rent(null, null, null, null, 0, default, default, default);
    }
}
