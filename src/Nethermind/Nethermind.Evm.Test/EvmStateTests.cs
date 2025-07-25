// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class EvmStateTests
    {
        [Test]
        public void Things_are_cold_to_start_with()
        {
            EvmState evmState = CreateEvmState();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            evmState.AccessTracker.IsCold(TestItem.AddressA).Should().BeTrue();
            evmState.AccessTracker.IsCold(storageCell).Should().BeTrue();
        }

        [Test]
        public void Can_warm_address_up_twice()
        {
            EvmState evmState = CreateEvmState();
            Address address = TestItem.AddressA;
            evmState.AccessTracker.WarmUp(address);
            evmState.AccessTracker.WarmUp(address);
            evmState.AccessTracker.IsCold(address).Should().BeFalse();
        }

        [Test]
        public void Can_warm_up_many()
        {
            EvmState evmState = CreateEvmState();
            for (int i = 0; i < TestItem.Addresses.Length; i++)
            {
                evmState.AccessTracker.WarmUp(TestItem.Addresses[i]);
                evmState.AccessTracker.WarmUp(new StorageCell(TestItem.Addresses[i], 1));
            }

            for (int i = 0; i < TestItem.Addresses.Length; i++)
            {
                evmState.AccessTracker.IsCold(TestItem.Addresses[i]).Should().BeFalse();
                evmState.AccessTracker.IsCold(new StorageCell(TestItem.Addresses[i], 1)).Should().BeFalse();
            }
        }

        [Test]
        public void Can_warm_storage_up_twice()
        {
            EvmState evmState = CreateEvmState();
            Address address = TestItem.AddressA;
            StorageCell storageCell = new(address, 1);
            evmState.AccessTracker.WarmUp(storageCell);
            evmState.AccessTracker.WarmUp(storageCell);
            evmState.AccessTracker.IsCold(storageCell).Should().BeFalse();
        }

        [Test]
        public void Nothing_to_commit()
        {
            EvmState parentEvmState = CreateEvmState();
            using (EvmState evmState = CreateEvmState(parentEvmState))
            {
                evmState.CommitToParent(parentEvmState);
            }
        }

        [Test]
        public void Nothing_to_restore()
        {
            EvmState parentEvmState = CreateEvmState();
            using EvmState evmState = CreateEvmState(parentEvmState);
        }

        [Test]
        public void Address_to_commit_keeps_it_warm()
        {
            EvmState parentEvmState = CreateEvmState();
            using (EvmState evmState = CreateEvmState(parentEvmState))
            {
                evmState.AccessTracker.WarmUp(TestItem.AddressA);
                evmState.CommitToParent(parentEvmState);
            }

            parentEvmState.AccessTracker.IsCold(TestItem.AddressA).Should().BeFalse();
        }

        [Test]
        public void Address_to_restore_keeps_it_cold()
        {
            EvmState parentEvmState = CreateEvmState();
            using (EvmState evmState = CreateEvmState(parentEvmState))
            {
                evmState.AccessTracker.WarmUp(TestItem.AddressA);
            }

            parentEvmState.AccessTracker.IsCold(TestItem.AddressA).Should().BeTrue();
        }

        [Test]
        public void Storage_to_commit_keeps_it_warm()
        {
            EvmState parentEvmState = CreateEvmState();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            using (EvmState evmState = CreateEvmState(parentEvmState))
            {
                evmState.AccessTracker.WarmUp(storageCell);
                evmState.CommitToParent(parentEvmState);
            }

            parentEvmState.AccessTracker.IsCold(storageCell).Should().BeFalse();
        }

        [Test]
        public void Storage_to_restore_keeps_it_cold()
        {
            EvmState parentEvmState = CreateEvmState();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            using (EvmState evmState = CreateEvmState(parentEvmState))
            {
                evmState.AccessTracker.WarmUp(storageCell);
            }

            parentEvmState.AccessTracker.IsCold(storageCell).Should().BeTrue();
        }

        [Test]
        public void Logs_are_committed()
        {
            EvmState parentEvmState = CreateEvmState();
            LogEntry logEntry = new(Address.Zero, Bytes.Empty, []);
            using (EvmState evmState = CreateEvmState(parentEvmState))
            {
                evmState.AccessTracker.Logs.Add(logEntry);
                evmState.CommitToParent(parentEvmState);
            }

            parentEvmState.AccessTracker.Logs.Contains(logEntry).Should().BeTrue();
        }

        [Test]
        public void Logs_are_restored()
        {
            EvmState parentEvmState = CreateEvmState();
            LogEntry logEntry = new(Address.Zero, Bytes.Empty, []);
            using (EvmState evmState = CreateEvmState(parentEvmState))
            {
                evmState.AccessTracker.Logs.Add(logEntry);
            }

            parentEvmState.AccessTracker.Logs.Contains(logEntry).Should().BeFalse();
        }

        [Test]
        public void Destroy_list_is_committed()
        {
            EvmState parentEvmState = CreateEvmState();
            using (EvmState evmState = CreateEvmState(parentEvmState))
            {
                evmState.AccessTracker.ToBeDestroyed(Address.Zero);
                evmState.CommitToParent(parentEvmState);
            }

            parentEvmState.AccessTracker.DestroyList.Contains(Address.Zero).Should().BeTrue();
        }

        [Test]
        public void Destroy_list_is_restored()
        {
            EvmState parentEvmState = CreateEvmState();
            using (EvmState evmState = CreateEvmState(parentEvmState))
            {
                evmState.AccessTracker.ToBeDestroyed(Address.Zero);
            }

            parentEvmState.AccessTracker.DestroyList.Contains(Address.Zero).Should().BeFalse();
        }

        [Test]
        public void Commit_adds_refunds()
        {
            EvmState parentEvmState = CreateEvmState();
            using (EvmState evmState = CreateEvmState(parentEvmState))
            {
                evmState.Refund = 333;
                evmState.CommitToParent(parentEvmState);
            }

            parentEvmState.Refund.Should().Be(333);
        }

        [Test]
        public void Restore_doesnt_add_refunds()
        {
            EvmState parentEvmState = CreateEvmState();
            using (EvmState evmState = CreateEvmState(parentEvmState))
            {
                evmState.Refund = 333;
            }

            parentEvmState.Refund.Should().Be(0);
        }

        [Test]
        public void Can_dispose_without_init()
        {
            EvmState evmState = CreateEvmState();
            evmState.Dispose();
        }

        [Test]
        public void Can_dispose_after_init()
        {
            EvmState evmState = CreateEvmState();
            evmState.InitializeStacks();
            evmState.Dispose();
        }

        private static EvmState CreateEvmState(EvmState parentEvmState = null, bool isContinuation = false) =>
            parentEvmState is null
                ? EvmState.RentTopLevel(10000,
                    ExecutionType.CALL,
                    new ExecutionEnvironment(),
                    new StackAccessTracker(),
                    Snapshot.Empty)
                : EvmState.RentFrame(10000,
                    0,
                    0,
                    ExecutionType.CALL,
                    false,
                    false,
                    new ExecutionEnvironment(),
                    parentEvmState.AccessTracker,
                    Snapshot.Empty);

        public class Context { }
    }
}
