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
using Nethermind.Int256;
using Nethermind.JsonRpc;
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
            var (userOperationPool, _, _) = GenerateUserOperationPool();

            UserOperation op = Build.A.UserOperation.WithTarget(Address.SystemUser).SignedAndResolved().TestObject;

            userOperationPool.AddUserOperation(op);

            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(1);
            userOperations[0].Should().BeEquivalentTo(op);
        }

        [Test]
        public void Can_evict_lower_gas_price_user_operation_if_full()
        {
            var (userOperationPool, _, _) = GenerateUserOperationPool(1);

            UserOperation op = Build.A.UserOperation.WithTarget(Address.SystemUser).SignedAndResolved().TestObject;
            UserOperation op2 = Build.A.UserOperation.WithTarget(Address.SystemUser).WithMaxFeePerGas(2).WithMaxPriorityFeePerGas(2).SignedAndResolved().TestObject;


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

            UserOperation op = Build.A.UserOperation.WithTarget(Address.SystemUser).SignedAndResolved().TestObject;

            userOperationPool.AddUserOperation(op);

            simulator.Received().Simulate(op, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
        }

        [Test]
        public void Evicted_user_operation_has_its_simulated_removed_automatically()
        {
            var (userOperationPool, simulator, _) = GenerateUserOperationPool(1);
            
            UserOperation op = Build.A.UserOperation.WithTarget(Address.SystemUser).SignedAndResolved().TestObject;
            UserOperation op2 = Build.A.UserOperation.WithTarget(Address.SystemUser).WithMaxFeePerGas(2).WithMaxPriorityFeePerGas(2).SignedAndResolved().TestObject;


            userOperationPool.AddUserOperation(op);
            simulator.Received().Simulate(op, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
            userOperationPool.GetUserOperations().Count().Should().Be(1);
            userOperationPool.GetUserOperations().Should().BeEquivalentTo(op);

            userOperationPool.AddUserOperation(op2);
            simulator.Received()
                .Simulate(op2, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
            userOperationPool.GetUserOperations().Count().Should().Be(1);
            userOperationPool.GetUserOperations().Should().BeEquivalentTo(op2);
        }

        private static IEnumerable<UserOperation> BadOperations
        {
            get
            {
                // if callGas < Gas Cost of Transaction
                yield return Build.A.UserOperation.WithTarget(Address.SystemUser).WithCallGas(1).SignedAndResolved().TestObject;
                // if target is zero address
                yield return Build.A.UserOperation.SignedAndResolved().TestObject;
                // if target is not a contract
                yield return Build.A.UserOperation.WithTarget(_notAnAddress).SignedAndResolved().TestObject;
                // if paymaster is not a contract
                yield return Build.A.UserOperation.WithPaymaster(_notAnAddress).SignedAndResolved().TestObject;
            }
        }
        
        // currently failing: issue with Keccak
        [TestCaseSource(nameof(BadOperations))]
        public void Does_not_accept_obviously_bad_user_operations_into_pool(UserOperation userOperation)
        {
            var (userOperationPool, _, _) = GenerateUserOperationPool(1);

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
        public void Bans_paymaster_if_it_uses_too_much_gas_for_simulation_too_many_times()
        {
            var (userOperationPool, simulator, blockTree) = GenerateUserOperationPool(10);
            UserOperation op = Build.A.UserOperation
                .WithTarget(Address.SystemUser)
                .SignedAndResolved()
                .TestObject;
                
            
            userOperationPool.AddUserOperation(op);
            
            for (int i = 0; i < 7; i++) 
            {
                blockTree.NewHeadBlock += Raise.EventWith(new object(), new BlockEventArgs(Core.Test.Builders.Build.A.Block.TestObject));
            }
            
            UserOperation op2 = Build.A.UserOperation.WithTarget(Address.SystemUser).SignedAndResolved().TestObject;
            
            userOperationPool.AddUserOperation(op2);
            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(0);
        }

        private (UserOperationPool, IUserOperationSimulator, IBlockTree) GenerateUserOperationPool(int capacity = 10)
        {
            UserOperationSortedPool userOperationSortedPool =
                new UserOperationSortedPool(capacity, CompareUserOperationsByDecreasingGasPrice.Default, LimboLogs.Instance);

            IStateProvider stateProvider = Substitute.For<IStateProvider>();
            stateProvider.GetBalance(Arg.Any<Address>()).Returns(1.Ether());
            stateProvider.AccountExists(Arg.Any<Address>()).Returns(true);
            stateProvider.IsContract(Arg.Any<Address>()).Returns(true);

            stateProvider.GetBalance(_notAnAddress).Returns(0.Ether());
            stateProvider.AccountExists(_notAnAddress).Returns(false);
            stateProvider.IsContract(_notAnAddress).Returns(false);

            IUserOperationSimulator simulator = Substitute.For<IUserOperationSimulator>();
            simulator.Simulate(Arg.Any<UserOperation>(), Arg.Any<BlockHeader>())
                .ReturnsForAnyArgs(x => Task.FromResult(ResultWrapper<Keccak>.Success(Keccak.Zero)));

            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.Head.Returns(Core.Test.Builders.Build.A.Block.TestObject);

            IAccountAbstractionConfig config = Substitute.For<IAccountAbstractionConfig>();
            config.SingletonContractAddress.Returns("0x8595dd9e0438640b5e1254f9df579ac12a86865f");
            
            IPeerManager peerManager = Substitute.For<IPeerManager>();

            IPaymasterThrottler paymasterThrottler = Substitute.For<PaymasterThrottler>();

            UserOperationPool userOperationPool = new(
                blockTree,
                stateProvider,
                paymasterThrottler,
                Substitute.For<ITimestamper>(),
                config,
                peerManager,
                userOperationSortedPool,
                simulator
            );
            return (userOperationPool, simulator, blockTree);
        }
    }
}
