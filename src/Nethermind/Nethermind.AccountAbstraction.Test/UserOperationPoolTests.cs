using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.Access;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public class UserOperationPoolTests
    {
        [Test]
        public void Can_add_user_operation_correctly()
        {
            var (userOperationPool, _, _) = GenerateUserOperationPool();

            UserOperation op = new(Address.SystemUser,
                Address.SystemUser,
                25_000,
                20_000,
                50,
                Address.Zero.Bytes,
                new Signature(0, 1, 1000),
                new AccessList(null));

            userOperationPool.AddUserOperation(op);

            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(1);
            userOperations[0].Should().BeEquivalentTo(op);
        }

        [Test]
        public void Can_evict_lower_gas_price_user_operation_if_full()
        {
            var (userOperationPool, _, _) = GenerateUserOperationPool(1);

            UserOperation op = new(Address.SystemUser,
                Address.SystemUser,
                25_000,
                20_000,
                20,
                Address.Zero.Bytes,
                new Signature(0, 1, 1000),
                new AccessList(null));
            UserOperation op2 = new(Address.SystemUser,
                Address.SystemUser,
                25_000,
                10_000,
                50,
                Address.Zero.Bytes,
                new Signature(0, 1, 1000),
                new AccessList(null));

            userOperationPool.AddUserOperation(op);
            userOperationPool.AddUserOperation(op2);

            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(1);
            userOperations[0].Should().BeEquivalentTo(op2);
        }

        [Test]
        public void Added_user_operation_gets_simulated()
        {
            var (userOperationPool, simulator, _) = GenerateUserOperationPool();

            UserOperation op = new(Address.SystemUser,
                Address.SystemUser,
                25_000,
                20_000,
                50,
                Address.Zero.Bytes,
                new Signature(0, 1, 1000),
                new AccessList(null));

            userOperationPool.AddUserOperation(op);

            simulator.Received().Simulate(op, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
        }

        [Test]
        public void Evicted_user_operation_has_its_simulated_removed_automatically()
        {
            var (userOperationPool, simulator, simulatedUserOperations) = GenerateUserOperationPool(1);

            UserOperation op = new(Address.SystemUser,
                Address.SystemUser,
                25_000,
                20_000,
                20,
                Address.Zero.Bytes,
                new Signature(0, 1, 1000),
                new AccessList(null));
            UserOperation op2 = new(Address.SystemUser,
                Address.SystemUser,
                25_000,
                10_000,
                50,
                Address.Zero.Bytes,
                new Signature(0, 1, 1000),
                new AccessList(null));

            userOperationPool.AddUserOperation(op);
            simulator.Received().Simulate(op, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
            simulatedUserOperations.Count.Should().Be(1);
            simulatedUserOperations.Keys.Should().BeEquivalentTo(op);

            userOperationPool.AddUserOperation(op2);
            simulator.Received()
                .Simulate(op2, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
            simulatedUserOperations.Count.Should().Be(1);
            simulatedUserOperations.Keys.Should().BeEquivalentTo(op2);
        }

        // add testcases here
        [TestCase(null)]
        public void Does_not_accept_obviously_bad_user_operations_into_pool(UserOperation userOperation)
        {
            
        }

        [Test]
        public void Deletes_simulated_op_if_op_gets_evicted_from_pool()
        {
            
        }

        [Test]
        public void Resimulates_ops_where_new_block_touched_op_access_list()
        {

        }

        [Test]
        public void Deleted_op_if_resimulation_caused_it_to_go_outside_access_list()
        {
            
        }

        [Test]
        public void Deletes_op_if_simulation_ever_fails()
        {
            
        }
        
        [Test]
        public void Deletes_op_if_resimulated_too_many_times()
        {
            
        }

        [Test]
        public void Bans_paymaster_if_it_uses_too_much_gas_for_simulation_too_many_times()
        {
            
        }

        private (UserOperationPool, IUserOperationSimulator, ConcurrentDictionary<UserOperation, SimulatedUserOperation>) GenerateUserOperationPool(int capacity = 10)
        {
            UserOperationSortedPool userOperationSortedPool =
                new UserOperationSortedPool(capacity, CompareUserOperationsByDecreasingGasPrice.Default, LimboLogs.Instance);
            ConcurrentDictionary<UserOperation, SimulatedUserOperation> simulatedUserOperations = new();

            IStateProvider stateProvider = Substitute.For<IStateProvider>();
            stateProvider.GetBalance(Arg.Any<Address>()).Returns(1.Ether());
            stateProvider.AccountExists(Arg.Any<Address>()).Returns(true);
            stateProvider.IsContract(Arg.Any<Address>()).Returns(true);

            IUserOperationSimulator simulator = Substitute.For<IUserOperationSimulator>();
            simulator.Simulate(Arg.Any<UserOperation>(), Arg.Any<BlockHeader>())
                .ReturnsForAnyArgs(x => Task.FromResult(new SimulatedUserOperation(x.Arg<UserOperation>(), true, 10)));

            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.Head.Returns(Build.A.Block.TestObject);

            UserOperationPool userOperationPool = new(
                blockTree,
                stateProvider,
                Substitute.For<ITimestamper>(),
                new AccessBlockTracer(Array.Empty<Address>()),
                Substitute.For<IAccountAbstractionConfig>(),
                new Dictionary<Address, int>(),
                new HashSet<Address>(), 
                userOperationSortedPool,
                simulator,
                simulatedUserOperations
            );
            return (userOperationPool, simulator, simulatedUserOperations);
        }
    }
}
