// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NeighbourMsgHandlerTests
    {
        private NeighbourMsgHandler _handler;
        private const int K = 16;
        private IPEndPoint _farAddress;
        private long _expirationTime;

        [SetUp]
        public void Setup()
        {
            _handler = new NeighbourMsgHandler(K);
            _farAddress = new IPEndPoint(IPAddress.Parse("192.168.1.1"), 30303);
            _expirationTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60; // 60 seconds in the future
        }

        [Test]
        public void Handle_should_return_true_when_processing_valid_message()
        {
            // Arrange
            var nodes = CreateNodes(5);
            var msg = new NeighborsMsg(_farAddress, _expirationTime, nodes);

            // Act
            bool result = _handler.Handle(msg);

            // Assert
            result.Should().BeTrue();
        }

        [Test]
        public void Handle_should_return_false_when_k_nodes_already_collected()
        {
            // Arrange
            // First, fill the handler with K nodes
            var initialNodes = CreateNodes(K);
            var initialMsg = new NeighborsMsg(_farAddress, _expirationTime, initialNodes);
            _handler.Handle(initialMsg);

            // Then try to add more nodes
            var additionalNodes = CreateNodes(5);
            var additionalMsg = new NeighborsMsg(_farAddress, _expirationTime, additionalNodes);

            // Act
            bool result = _handler.Handle(additionalMsg);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public void Handle_should_return_false_when_adding_more_than_k_nodes()
        {
            // Arrange
            // First, fill the handler with K-1 nodes
            var initialNodes = CreateNodes(K - 1);
            var initialMsg = new NeighborsMsg(_farAddress, _expirationTime, initialNodes);
            _handler.Handle(initialMsg);

            // Then try to add more than 1 node
            var additionalNodes = CreateNodes(2);
            var additionalMsg = new NeighborsMsg(_farAddress, _expirationTime, additionalNodes);

            // Act
            bool result = _handler.Handle(additionalMsg);

            // Assert
            result.Should().BeFalse();
        }

        [Test]
        public async Task TaskCompletionSource_should_complete_when_k_nodes_collected()
        {
            // Arrange
            var nodes = CreateNodes(K);
            var msg = new NeighborsMsg(_farAddress, _expirationTime, nodes);

            // Act
            _handler.Handle(msg);
            
            // Create a task that will complete when the TaskCompletionSource completes
            Task<Node[]> task = _handler.TaskCompletionSource.Task;
            
            // Wait for the task to complete with a timeout
            var completedTask = await Task.WhenAny(task, Task.Delay(100));

            // Assert
            completedTask.Should().Be(task);
            task.IsCompleted.Should().BeTrue();
            task.Result.Should().HaveCount(K);
            task.Result.Should().BeEquivalentTo(nodes);
        }

        [Test]
        public async Task TaskCompletionSource_should_complete_after_timeout_when_less_than_k_nodes_collected()
        {
            // Arrange
            var nodes = CreateNodes(K - 5); // Less than K nodes
            var msg = new NeighborsMsg(_farAddress, _expirationTime, nodes);

            // Act
            _handler.Handle(msg);
            
            // Create a task that will complete when the TaskCompletionSource completes
            Task<Node[]> task = _handler.TaskCompletionSource.Task;
            
            // Wait for the task to complete with a timeout longer than the handler's timeout
            var completedTask = await Task.WhenAny(task, Task.Delay(1500)); // Handler timeout is 1 second

            // Assert
            completedTask.Should().Be(task);
            task.IsCompleted.Should().BeTrue();
            task.Result.Should().HaveCount(K - 5);
            task.Result.Should().BeEquivalentTo(nodes);
        }

        [Test]
        public void Handle_should_accumulate_nodes_from_multiple_messages()
        {
            // Arrange
            var firstBatch = CreateNodes(5);
            var secondBatch = CreateNodes(5, 5); // Start from index 5 to create different nodes
            
            var firstMsg = new NeighborsMsg(_farAddress, _expirationTime, firstBatch);
            var secondMsg = new NeighborsMsg(_farAddress, _expirationTime, secondBatch);

            // Act
            _handler.Handle(firstMsg);
            _handler.Handle(secondMsg);
            
            // Assert
            _handler.TaskCompletionSource.Task.Wait(100); // Give a small timeout for any async operations
            var result = _handler.TaskCompletionSource.Task.Result;
            
            result.Should().HaveCount(10);
            result.Should().Contain(firstBatch);
            result.Should().Contain(secondBatch);
        }

        [Test]
        public void Handle_should_only_initiate_timeout_once()
        {
            // Arrange
            var firstBatch = CreateNodes(3);
            var secondBatch = CreateNodes(3, 3); // Start from index 3 to create different nodes
            
            var firstMsg = new NeighborsMsg(_farAddress, _expirationTime, firstBatch);
            var secondMsg = new NeighborsMsg(_farAddress, _expirationTime, secondBatch);

            // Act
            _handler.Handle(firstMsg); // This should initiate the timeout
            Task.Delay(500).Wait(); // Wait a bit, but less than the timeout
            _handler.Handle(secondMsg); // This should not initiate another timeout
            
            // Assert
            // We can't directly test that the timeout is only initiated once,
            // but we can verify that the nodes are accumulated correctly
            Task.Delay(1500).Wait(); // Wait for the timeout to complete
            var result = _handler.TaskCompletionSource.Task.Result;
            
            result.Should().HaveCount(6);
            result.Should().Contain(firstBatch);
            result.Should().Contain(secondBatch);
        }

        private ArraySegment<Node> CreateNodes(int count, int startIndex = 0)
        {
            var nodes = new Node[count];
            for (int i = 0; i < count; i++)
            {
                // Create a 64-byte (128 hex chars) public key by padding with zeros
                string hexString = $"0x{(i + startIndex).ToString().PadLeft(2, '0')}".PadRight(130, '0');
                var publicKey = new PublicKey(hexString);
                nodes[i] = new Node(publicKey, $"192.168.1.{i + startIndex + 10}", 30303);
            }
            return new ArraySegment<Node>(nodes);
        }
    }
}
