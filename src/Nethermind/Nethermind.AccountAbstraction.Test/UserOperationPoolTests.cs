using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using Nethermind.Evm.Tracing.Access;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public class UserOperationPoolTests
    {
        [Test]
        public void Can_add_user_operation_correctly()
        {
            UserOperationPool userOperationPool = GenerateUserOperationPool();
            
            UserOperation op = new UserOperation(Address.Zero, 
                Address.Zero, 
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
        
        [Test]
        public void Can_evict_lower_gas_price_user_operation_if_full()
        {
            UserOperationPool userOperationPool = GenerateUserOperationPool(1);
            
            UserOperation op = new(Address.Zero, 
                Address.Zero, 
                25_000, 
                20_000, 
                20, 
                Address.Zero.Bytes,
                new Signature(0, 1, 1000));
            UserOperation op2 = new(Address.Zero, 
                Address.Zero, 
                25_000, 
                10_000, 
                50, 
                Address.Zero.Bytes,
                new Signature(0, 1, 1000));

            userOperationPool.AddUserOperation(op);
            userOperationPool.AddUserOperation(op2);

            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(1);
            userOperations[0].Should().BeEquivalentTo(op2);
        }

        private UserOperationPool GenerateUserOperationPool(int capacity = 10)
        {
            UserOperationSortedPool userOperationSortedPool =
                new UserOperationSortedPool(capacity, CompareUserOperationsByDecreasingGasPrice.Default, LimboLogs.Instance);
            ConcurrentDictionary<UserOperation, SimulatedUserOperation> simulatedUserOperations = new();
            UserOperationPool userOperationPool = new(
                Substitute.For<IBlockTree>(),
                Substitute.For<IStateProvider>(),
                Substitute.For<ITimestamper>(),
                Substitute.For<IBlockchainProcessor>(),
                new AccessBlockTracer(Array.Empty<Address>()),
                Substitute.For<IAccountAbstractionConfig>(),
                new Dictionary<Address, int>(),
                new HashSet<Address>(), 
                userOperationSortedPool,
                Substitute.For<IUserOperationSimulator>(),
                simulatedUserOperations
            );
            return userOperationPool;
        }
    }
}
