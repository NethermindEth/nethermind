// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class CallFrameTests
    {
        private AccessTrackingState TrackingState;

        [SetUp]
        public void SetUp()
        {
            TrackingState = AccessTrackingState.RentState();
        }

        [TearDown]
        public void TearDown()
        {
            AccessTrackingState.ResetAndReturn(TrackingState);
        }

        [Test]
        public void Things_are_cold_to_start_with()
        {
            CallFrame<EthereumGasPolicy> callFrame = CreateCallFrame();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            TrackingState.IsCold(TestItem.AddressA).Should().BeTrue();
            TrackingState.IsCold(storageCell).Should().BeTrue();
        }

        [Test]
        public void Can_warm_address_up_twice()
        {
            CallFrame<EthereumGasPolicy> callFrame = CreateCallFrame();
            Address address = TestItem.AddressA;
            TrackingState.WarmUp(address);
            TrackingState.WarmUp(address);
            TrackingState.IsCold(address).Should().BeFalse();
        }

        [Test]
        public void Can_warm_up_many()
        {
            CallFrame<EthereumGasPolicy> callFrame = CreateCallFrame();
            for (int i = 0; i < TestItem.Addresses.Length; i++)
            {
                TrackingState.WarmUp(TestItem.Addresses[i]);
                TrackingState.WarmUp(new StorageCell(TestItem.Addresses[i], 1));
            }

            for (int i = 0; i < TestItem.Addresses.Length; i++)
            {
                TrackingState.IsCold(TestItem.Addresses[i]).Should().BeFalse();
                TrackingState.IsCold(new StorageCell(TestItem.Addresses[i], 1)).Should().BeFalse();
            }
        }

        [Test]
        public void Can_warm_storage_up_twice()
        {
            CallFrame<EthereumGasPolicy> callFrame = CreateCallFrame();
            Address address = TestItem.AddressA;
            StorageCell storageCell = new(address, 1);
            TrackingState.WarmUp(storageCell);
            TrackingState.WarmUp(storageCell);
            TrackingState.IsCold(storageCell).Should().BeFalse();
        }

        [Test]
        public void Nothing_to_commit()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            using (CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame))
            {
                childFrame.CommitToParent(parentCallFrame);
            }
        }

        [Test]
        public void Nothing_to_restore()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame);
            childFrame.RestoreAccessTracker(TrackingState);
            childFrame.Dispose();
        }

        [Test]
        public void Address_to_commit_keeps_it_warm()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            using (CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame))
            {
                TrackingState.WarmUp(TestItem.AddressA);
                childFrame.CommitToParent(parentCallFrame);
            }

            TrackingState.IsCold(TestItem.AddressA).Should().BeFalse();
        }

        [Test]
        public void Address_to_restore_keeps_it_cold()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame);
            TrackingState.WarmUp(TestItem.AddressA);
            childFrame.RestoreAccessTracker(TrackingState);
            childFrame.Dispose();

            TrackingState.IsCold(TestItem.AddressA).Should().BeTrue();
        }

        [Test]
        public void Storage_to_commit_keeps_it_warm()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            using (CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame))
            {
                TrackingState.WarmUp(storageCell);
                childFrame.CommitToParent(parentCallFrame);
            }

            TrackingState.IsCold(storageCell).Should().BeFalse();
        }

        [Test]
        public void Storage_to_restore_keeps_it_cold()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame);
            TrackingState.WarmUp(storageCell);
            childFrame.RestoreAccessTracker(TrackingState);
            childFrame.Dispose();

            TrackingState.IsCold(storageCell).Should().BeTrue();
        }

        [Test]
        public void Logs_are_committed()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            LogEntry logEntry = new(Address.Zero, Bytes.Empty, []);
            using (CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame))
            {
                TrackingState.Logs.Add(logEntry);
                childFrame.CommitToParent(parentCallFrame);
            }

            TrackingState.Logs.Contains(logEntry).Should().BeTrue();
        }

        [Test]
        public void Logs_are_restored()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            LogEntry logEntry = new(Address.Zero, Bytes.Empty, []);
            CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame);
            TrackingState.Logs.Add(logEntry);
            childFrame.RestoreAccessTracker(TrackingState);
            childFrame.Dispose();

            TrackingState.Logs.Contains(logEntry).Should().BeFalse();
        }

        [Test]
        public void Destroy_list_is_committed()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            using (CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame))
            {
                TrackingState.ToBeDestroyed(Address.Zero);
                childFrame.CommitToParent(parentCallFrame);
            }

            TrackingState.DestroyList.Contains(Address.Zero).Should().BeTrue();
        }

        [Test]
        public void Destroy_list_is_restored()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame);
            TrackingState.ToBeDestroyed(Address.Zero);
            childFrame.RestoreAccessTracker(TrackingState);
            childFrame.Dispose();

            TrackingState.DestroyList.Contains(Address.Zero).Should().BeFalse();
        }

        [Test]
        public void Commit_adds_refunds()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            using (CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame))
            {
                childFrame.Refund = 333;
                childFrame.CommitToParent(parentCallFrame);
            }

            parentCallFrame.Refund.Should().Be(333);
        }

        [Test]
        public void Restore_does_not_add_refunds()
        {
            CallFrame<EthereumGasPolicy> parentCallFrame = CreateCallFrame();
            CallFrame<EthereumGasPolicy> childFrame = CreateCallFrame(parentCallFrame);
            childFrame.Refund = 333;
            childFrame.RestoreAccessTracker(TrackingState);
            childFrame.Dispose();

            parentCallFrame.Refund.Should().Be(0);
        }

        [Test]
        public void Can_dispose_without_init()
        {
            CallFrame<EthereumGasPolicy> callFrame = CreateCallFrame();
            callFrame.Dispose();
        }

        [Test]
        public void Can_dispose_after_init()
        {
            CallFrame<EthereumGasPolicy> callFrame = CreateCallFrame();
            callFrame.InitializeStacks(null, default, out _);
            callFrame.Dispose();
        }

        private CallFrame<EthereumGasPolicy> CreateCallFrame(CallFrame<EthereumGasPolicy> parentCallFrame = null, bool isContinuation = false) =>
            parentCallFrame is null
                ? CallFrame<EthereumGasPolicy>.RentTopLevel(EthereumGasPolicy.FromLong(10000),
                    ExecutionType.CALL,
                    null, null, null, null, default, default,
                    default,
                    TrackingState,
                    Snapshot.Empty)
                : CallFrame<EthereumGasPolicy>.Rent(EthereumGasPolicy.FromLong(10000),
                    0,
                    0,
                    ExecutionType.CALL,
                    false,
                    false,
                    null, null, null, null, default, default,
                    parentCallFrame.AccessTracker,
                    TrackingState,
                    Snapshot.Empty);
    }
}
