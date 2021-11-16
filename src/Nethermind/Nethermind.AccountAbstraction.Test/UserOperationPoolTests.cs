using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;

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

            UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).SignedAndResolved().TestObject;

            userOperationPool.AddUserOperation(op);

            UserOperation[] userOperations = userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(1);
            userOperations[0].Should().BeEquivalentTo(op);
        }

        [Test]
        public void Can_evict_lower_gas_price_user_operation_if_full()
        {
            var (userOperationPool, _, _) = GenerateUserOperationPool(1);

            UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).SignedAndResolved().TestObject;
            UserOperation op2 = Build.A.UserOperation.WithSender(Address.SystemUser).WithMaxFeePerGas(2).WithMaxPriorityFeePerGas(2).SignedAndResolved().TestObject;


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

            UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).SignedAndResolved().TestObject;

            userOperationPool.AddUserOperation(op);

            simulator.Received().Simulate(op, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
        }

        [Test]
        public void Evicted_user_operation_has_its_simulated_removed_automatically()
        {
            var (userOperationPool, simulator, _) = GenerateUserOperationPool(1);
            
            UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).SignedAndResolved().TestObject;
            UserOperation op2 = Build.A.UserOperation.WithSender(Address.SystemUser).WithMaxFeePerGas(2).WithMaxPriorityFeePerGas(2).SignedAndResolved().TestObject;


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
        
        [Test]
        public void should_add_user_operations_concurrently()
        {
            int capacity = 2048;
            var (userOperationPool, _, _) = GenerateUserOperationPool(capacity);

            Parallel.ForEach(TestItem.PrivateKeys, k =>
            {
                for (int i = 0; i < 100; i++)
                {
                    UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)i).SignedAndResolved(k).TestObject;
                    userOperationPool.AddUserOperation(op);
                }
            });

            userOperationPool.GetUserOperations().Count().Should().Be(capacity);
        }
        
        [Test]
        public async Task should_remove_user_operations_concurrently()
        {
            int capacity = 4096;
            var (userOperationPool, _, _) = GenerateUserOperationPool(capacity);
            
            int maxTryCount = 5;
            for (int i = 0; i < maxTryCount; ++i)
            {
                Parallel.ForEach(TestItem.PrivateKeys, k =>
                {
                    for (int j = 0; j < 10; j++)
                    {
                        UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)j)
                            .SignedAndResolved(k).TestObject;
                        userOperationPool.AddUserOperation(op);
                    }
                });

                userOperationPool.GetUserOperations().Should().HaveCount(TestItem.PrivateKeys.Length * 10);
                UserOperation[] opsForFirstTask = userOperationPool.GetUserOperations().Where(o => o.Nonce == 8).ToArray();
                UserOperation[] opsForSecondTask = userOperationPool.GetUserOperations().Where(o => o.Nonce == 6).ToArray();
                UserOperation[] opsForThirdTask = userOperationPool.GetUserOperations().Where(o => o.Nonce == 7).ToArray();
                opsForFirstTask.Should().HaveCount(TestItem.PrivateKeys.Length);
                Task firstTask = Task.Run(() => DeleteOpsFromPool(opsForFirstTask));
                Task secondTask = Task.Run(() => DeleteOpsFromPool(opsForSecondTask));
                Task thirdTask = Task.Run(() => DeleteOpsFromPool(opsForThirdTask));
                await Task.WhenAll(firstTask, secondTask, thirdTask);
                userOperationPool.GetUserOperations().Should().HaveCount(TestItem.PrivateKeys.Length * 7);
            }
            

            void DeleteOpsFromPool(UserOperation[] ops)
            {
                foreach (UserOperation op in ops)
                {
                    userOperationPool.RemoveUserOperation(op);
                }
            }
        }

        [Test]
        public void should_add_peers()
        {
            var (userOperationPool, _, _) = GenerateUserOperationPool(100);
            IList<IUserOperationPoolPeer> peers = GetPeers();

            foreach (IUserOperationPoolPeer peer in peers)
            {
                userOperationPool.AddPeer(peer);
            }
        }

        [Test]
        public void should_delete_peers()
        {
            var (userOperationPool, _, _) = GenerateUserOperationPool(100);
            IList<IUserOperationPoolPeer> peers = GetPeers();

            foreach (IUserOperationPoolPeer peer in peers)
            {
                userOperationPool.AddPeer(peer);
            }

            foreach (IUserOperationPoolPeer peer in peers)
            {
                userOperationPool.RemovePeer(peer.Id);
            }
        }
        
        [Test]
        public void should_notify_added_peer_about_ops_in_UOpPool()
        {
            var (userOperationPool, _, _) = GenerateUserOperationPool();
            UserOperation op = Build.A.UserOperation.SignedAndResolved().TestObject;
            userOperationPool.AddUserOperation(op);
            IUserOperationPoolPeer uopPoolPeer = Substitute.For<IUserOperationPoolPeer>();
            uopPoolPeer.Id.Returns(TestItem.PublicKeyA);
            userOperationPool.AddPeer(uopPoolPeer);
            uopPoolPeer.Received().SendNewUserOperations(Arg.Any<IEnumerable<UserOperation>>());
        }
        
        [Test]
        public void should_send_to_peers_newly_added_uop()
        {
            var (userOperationPool, _, _) = GenerateUserOperationPool();
            IUserOperationPoolPeer uopPoolPeer = Substitute.For<IUserOperationPoolPeer>();
            uopPoolPeer.Id.Returns(TestItem.PublicKeyA);
            userOperationPool.AddPeer(uopPoolPeer);
            UserOperation op = Build.A.UserOperation.WithSender(TestItem.AddressA).SignedAndResolved().TestObject;
            userOperationPool.AddUserOperation(op);
            uopPoolPeer.Received().SendNewUserOperation(op);
        }

        private IList<IUserOperationPoolPeer> GetPeers(int limit = 100)
        {
            IList<IUserOperationPoolPeer> peers = new List<IUserOperationPoolPeer>();
            for (int i = 0; i < limit; i++)
            {
                PrivateKey privateKey = new((i + 1).ToString("x64"));
                peers.Add(GetPeer(privateKey.PublicKey));
            }

            return peers;
        }
        private IUserOperationPoolPeer GetPeer(PublicKey publicKey)
        {
            IUserOperationPoolPeer peer = Substitute.For<IUserOperationPoolPeer>();
            peer.Id.Returns(publicKey);
            
            return peer;
        }

        private static IEnumerable<UserOperation> BadOperations
        {
            get
            {
                // if callGas < Gas Cost of Transaction
                yield return Build.A.UserOperation.WithSender(Address.SystemUser).WithCallGas(1).SignedAndResolved().TestObject;
                // if target is zero address
                yield return Build.A.UserOperation.SignedAndResolved().TestObject;
                // if target is not a contract
                yield return Build.A.UserOperation.WithSender(_notAnAddress).SignedAndResolved().TestObject;
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
            config.EntryPointContractAddress.Returns("0x8595dd9e0438640b5e1254f9df579ac12a86865f");
            config.UserOperationPoolSize.Returns(capacity);
            
            IPaymasterThrottler paymasterThrottler = Substitute.For<PaymasterThrottler>();

            IReceiptFinder receiptFinder = Substitute.For<IReceiptFinder>();

            UserOperationPool userOperationPool = new(
                config,
                blockTree,
                new Address(config.EntryPointContractAddress), 
                NullLogger.Instance, 
                paymasterThrottler, 
                receiptFinder, 
                Substitute.For<ISigner>(), 
                stateProvider, 
                Substitute.For<ITimestamper>(), 
                simulator, 
                userOperationSortedPool);
            return (userOperationPool, simulator, blockTree);
        }
    }
}
