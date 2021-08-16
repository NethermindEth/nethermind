using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.Access;
using Nethermind.Int256;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public class UserOperationPoolTests
    {
        private static Address _notAnAddress = new("0x373f2D08b1C195fF08B9AbEdE3C78575FAAC2aCf");
        
        [Test]
        public void Can_add_user_operation_correctly()
        {
            var (userOperationPool, _, _, _) = GenerateUserOperationPool();

            UserOperation op = new(Address.SystemUser,
                0,
                Address.Zero.Bytes,
                25_000,
                50,
                50, 
                Address.SystemUser, 
                Address.SystemUser, 
                new Signature(0, 1, 1000),
                new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));

            userOperationPool.AddUserOperation(op);

            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(1);
            userOperations[0].Should().BeEquivalentTo(op);
        }

        [Test]
        public void Can_evict_lower_gas_price_user_operation_if_full()
        {
            var (userOperationPool, _, _, _) = GenerateUserOperationPool(1);

            UserOperation op = new(Address.SystemUser,
                0,
                Address.Zero.Bytes,
                25_000,
                20,
                20, 
                Address.SystemUser, 
                Address.SystemUser, 
                new Signature(0, 1, 1000),
                new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));
            UserOperation op2 = new(Address.SystemUser,
                0,
                Address.Zero.Bytes,
                25_000,
                50,
                50, 
                Address.SystemUser, 
                Address.SystemUser, 
                new Signature(0, 1, 1000),
                new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));

            userOperationPool.AddUserOperation(op);
            userOperationPool.AddUserOperation(op2);

            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(1);
            userOperations[0].Should().BeEquivalentTo(op2);
        }

        [Test]
        public void Added_user_operation_gets_simulated()
        {
            var (userOperationPool, simulator, _, _) = GenerateUserOperationPool();

            UserOperation op = new(Address.SystemUser,
                0,
                Address.Zero.Bytes,
                25_000,
                50,
                50, 
                Address.SystemUser, 
                Address.SystemUser, 
                new Signature(0, 1, 1000),
                new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));

            userOperationPool.AddUserOperation(op);

            simulator.Received().Simulate(op, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
        }

        [Test]
        public void Evicted_user_operation_has_its_simulated_removed_automatically()
        {
            var (userOperationPool, simulator, simulatedUserOperations, _) = GenerateUserOperationPool(1);
            UserOperation op = new(Address.SystemUser,
                0,
                Address.Zero.Bytes,
                25_000,
                20,
                20, 
                Address.SystemUser, 
                Address.SystemUser, 
                new Signature(0, 1, 1000),
                new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));
            UserOperation op2 = new(Address.SystemUser,
                0,
                Address.Zero.Bytes,
                25_000,
                50,
                50, 
                Address.SystemUser, 
                Address.SystemUser, 
                new Signature(0, 1, 1000),
                new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));

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

        private static IEnumerable<UserOperation> BadOperations
        {
            get
            {
                // if callGas < Gas Cost of Transaction
                yield return new(Address.SystemUser,
                    0,
                    Address.Zero.Bytes,
                    20_000,
                    50,
                    50, 
                    Address.SystemUser, 
                    Address.SystemUser, 
                    new Signature(0, 1, 1000),
                    new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));
                // if target is zero address
                yield return new(Address.Zero,
                    0,
                    Address.Zero.Bytes,
                    25_000,
                    50,
                    50, 
                    Address.SystemUser, 
                    Address.SystemUser, 
                    new Signature(0, 1, 1000),
                    new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));
                // if target is not a contract
                yield return new(_notAnAddress,
                    0,
                    Address.Zero.Bytes,
                    25_000,
                    50,
                    50, 
                    Address.SystemUser, 
                    Address.SystemUser, 
                    new Signature(0, 1, 1000),
                    new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));
                // if paymaster is not a contract
                yield return new(Address.SystemUser,
                    0,
                    Address.Zero.Bytes,
                    25_000,
                    50,
                    50, 
                    _notAnAddress, 
                    Address.SystemUser, 
                    new Signature(0, 1, 1000),
                    new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));
            }
        }
        
        // currently failing: issue with Keccak
        [TestCaseSource(nameof(BadOperations))]
        public void Does_not_accept_obviously_bad_user_operations_into_pool(UserOperation userOperation)
        {
            var (userOperationPool, _, _, _) = GenerateUserOperationPool(1);

            userOperationPool.AddUserOperation(userOperation);
            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(0);
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
        
        // currently failing
        [Test]
        public void Deletes_op_if_resimulated_too_many_times()
        {
            var (userOperationPool, simulator, simulatedUserOperations, _) = GenerateUserOperationPool(10);
            UserOperation op = new(Address.SystemUser,
                0,
                Address.Zero.Bytes,
                25_000,
                50,
                50, 
                Address.SystemUser, 
                Address.SystemUser, 
                new Signature(0, 1, 1000),
                new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));

            userOperationPool.AddUserOperation(op);
            
            for (int i = 0; i < 7; i++) 
            {
                simulator.Received()
                    .Simulate(op, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
            }
            
            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(0);
            Console.WriteLine(op.ResimulationCounter);
        }

        // currently failing
        [Test]
        public void Bans_paymaster_if_it_uses_too_much_gas_for_simulation_too_many_times()
        {
            var (userOperationPool, simulator, simulatedUserOperations, blockTree) = GenerateUserOperationPool(10);
            UserOperation op = new(Address.SystemUser,
                0,
                Address.Zero.Bytes,
                25_000,
                50,
                50, 
                Address.SystemUser, 
                Address.SystemUser, 
                new Signature(0, 1, 1000),
                new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>{{new("0x0000000000000000000000000000000000000001"), new HashSet<UInt256>{0}}}));
            
            userOperationPool.AddUserOperation(op);
            
            for (int i = 0; i < 7; i++) 
            {
                blockTree.NewHeadBlock += Raise.EventWith(new object(), new BlockEventArgs(Build.A.Block.TestObject));
            }
            
            UserOperation op2 = new(Address.SystemUser,
                0,
                Address.Zero.Bytes,
                25_000,
                50,
                50, 
                Address.SystemUser, 
                Address.SystemUser, 
                new Signature(0, 1, 1000),
                new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>()));
            
            userOperationPool.AddUserOperation(op2);
            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(0);
        }

        private (UserOperationPool, IUserOperationSimulator, ConcurrentDictionary<UserOperation, SimulatedUserOperation>, IBlockTree) GenerateUserOperationPool(int capacity = 10)
        {
            UserOperationSortedPool userOperationSortedPool =
                new UserOperationSortedPool(capacity, CompareUserOperationsByDecreasingGasPrice.Default, LimboLogs.Instance);
            ConcurrentDictionary<UserOperation, SimulatedUserOperation> simulatedUserOperations = new();

            IStateProvider stateProvider = Substitute.For<IStateProvider>();
            stateProvider.GetBalance(Arg.Any<Address>()).Returns(1.Ether());
            stateProvider.AccountExists(Arg.Any<Address>()).Returns(true);
            stateProvider.IsContract(Arg.Any<Address>()).Returns(true);

            stateProvider.GetBalance(_notAnAddress).Returns(0.Ether());
            stateProvider.AccountExists(_notAnAddress).Returns(false);
            stateProvider.IsContract(_notAnAddress).Returns(false);

            IUserOperationSimulator simulator = Substitute.For<IUserOperationSimulator>();
            simulator.Simulate(Arg.Any<UserOperation>(), Arg.Any<BlockHeader>())
                .ReturnsForAnyArgs(x => Task.FromResult(new SimulatedUserOperation(x.Arg<UserOperation>(), true, 10)));

            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.Head.Returns(Build.A.Block.TestObject);

            IAccountAbstractionConfig config = Substitute.For<IAccountAbstractionConfig>();
            config.SingletonContractAddress.Returns("0x8595dd9e0438640b5e1254f9df579ac12a86865f");
            config.MaxResimulations.Returns(5);

            IAccessListSource accessListSource = Substitute.For<IAccessListSource>();

            accessListSource.AccessList.Returns(new AccessList(
                new Dictionary<Address, IReadOnlySet<UInt256>> {{new("0x0000000000000000000000000000000000000001"), new HashSet<UInt256> {0}}}));
            
            IPeerManager peerManager = Substitute.For<IPeerManager>();

            UserOperationPool userOperationPool = new(
                blockTree,
                stateProvider,
                Substitute.For<ITimestamper>(),
                accessListSource,
                config,
                new Dictionary<Address, int>(),
                new HashSet<Address>(), 
                peerManager,
                userOperationSortedPool,
                simulator,
                simulatedUserOperations
            );
            return (userOperationPool, simulator, simulatedUserOperations, blockTree);
        }
    }
}
