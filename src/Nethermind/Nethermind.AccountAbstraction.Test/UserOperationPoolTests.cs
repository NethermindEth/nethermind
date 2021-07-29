using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using FluentAssertions;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using NUnit.Framework.Internal.Commands;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public class UserOperationPoolTests
    {
        [Test]
        public void Can_add_userOperation_correctly()
        {
            UserOperationSortedPool userOperationSortedPool =
                new UserOperationSortedPool(10, CompareUserOperationsByGasPrice.Default, LimboLogs.Instance);
            ConcurrentDictionary<UserOperation, SimulatedUserOperationContext> simulatedUserOperations = new();
            UserOperationPool userOperationPool = new(
                Substitute.For<IBlockTree>(),
                Substitute.For<IStateProvider>(),
                Substitute.For<IBlockchainProcessor>(),
                userOperationSortedPool,
                Substitute.For<IUserOperationSimulator>(),
                new SimulatedUserOperationSource(simulatedUserOperations),
                simulatedUserOperations
            );

            UserOperation op = new UserOperation(Address.Zero, 
                25_000, 
                20_000, 
                50, 
                Address.Zero.Bytes,
                new Signature(0, 1, 1000));

            userOperationPool.AddUserOperation(op);

            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(1);
            userOperations[0].Should().BeEquivalentTo(op);
        }
    }
}
