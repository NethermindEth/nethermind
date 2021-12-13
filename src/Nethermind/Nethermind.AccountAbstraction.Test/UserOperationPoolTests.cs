﻿using System.Collections.Generic;
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
        private IUserOperationPool _userOperationPool = Substitute.For<IUserOperationPool>();
        private IUserOperationSimulator _simulator = Substitute.For<IUserOperationSimulator>();
        private IBlockTree _blockTree = Substitute.For<IBlockTree>();
        private IReceiptFinder _receiptFinder = Substitute.For<IReceiptFinder>();
        private IStateProvider _stateProvider = Substitute.For<IStateProvider>();
        private readonly ISigner _signer = Substitute.For<ISigner>();
        private readonly Keccak _userOperationEventTopic = new("0xc27a60e61c14607957b41fa2dad696de47b2d80e390d0eaaf1514c0cd2034293");
        private readonly string _entryPointContractAddress = "0x8595dd9e0438640b5e1254f9df579ac12a86865f";
        private static Address _notAnAddress = new("0x373f2D08b1C195fF08B9AbEdE3C78575FAAC2aCf");
        
        [Test]
        public void Can_add_user_operation_correctly()
        {
            _userOperationPool = GenerateUserOperationPool();

            UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).SignedAndResolved().TestObject;

            _userOperationPool.AddUserOperation(op);

            UserOperation[] userOperations = _userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(1);
            userOperations[0].Should().BeEquivalentTo(op);
        }

        [Test]
        public void Can_evict_lower_gas_price_user_operation_if_full()
        {
            _userOperationPool = GenerateUserOperationPool(1);

            UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).SignedAndResolved().TestObject;
            UserOperation op2 = Build.A.UserOperation.WithSender(Address.SystemUser).WithMaxFeePerGas(2).WithMaxPriorityFeePerGas(2).SignedAndResolved().TestObject;


            _userOperationPool.AddUserOperation(op);
            _userOperationPool.AddUserOperation(op2);

            UserOperation[] userOperations = _userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(1);
            userOperations[0].Should().BeEquivalentTo(op2);
        }

        [Test]
        public void Added_user_operation_gets_simulated()
        {
            _userOperationPool = GenerateUserOperationPool();

            UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).SignedAndResolved().TestObject;

            _userOperationPool.AddUserOperation(op);

            _simulator.Received().Simulate(op, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
        }

        [Test]
        public void Evicted_user_operation_has_its_simulated_removed_automatically()
        {
            _userOperationPool = GenerateUserOperationPool(1);
            
            UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).SignedAndResolved().TestObject;
            UserOperation op2 = Build.A.UserOperation.WithSender(Address.SystemUser).WithMaxFeePerGas(2).WithMaxPriorityFeePerGas(2).SignedAndResolved().TestObject;

            IEnumerable<UserOperation> expectedOp = new[] { op };
            IEnumerable<UserOperation> expectedOp2 = new[] { op2 };

            _userOperationPool.AddUserOperation(op);
            _simulator.Received().Simulate(op, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
            _userOperationPool.GetUserOperations().Count().Should().Be(1);
            _userOperationPool.GetUserOperations().Should().BeEquivalentTo(expectedOp);

            _userOperationPool.AddUserOperation(op2);
            _simulator.Received()
                .Simulate(op2, Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>(), Arg.Any<UInt256>());
            _userOperationPool.GetUserOperations().Count().Should().Be(1);
            _userOperationPool.GetUserOperations().Should().BeEquivalentTo(expectedOp2);
        }
        
        [Test]
        public void should_add_user_operations_concurrently()
        {
            int capacity = 2048;
            _userOperationPool = GenerateUserOperationPool(capacity);

            Parallel.ForEach(TestItem.PrivateKeys, k =>
            {
                for (int i = 0; i < 100; i++)
                {
                    UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)i).SignedAndResolved(k).TestObject;
                    _userOperationPool.AddUserOperation(op);
                }
            });

            _userOperationPool.GetUserOperations().Count().Should().Be(capacity);
        }
        
        [Test]
        public async Task should_remove_user_operations_concurrently()
        {
            int capacity = 4096;
            _userOperationPool = GenerateUserOperationPool(capacity);
            
            int maxTryCount = 5;
            for (int i = 0; i < maxTryCount; ++i)
            {
                Parallel.ForEach(TestItem.PrivateKeys, k =>
                {
                    for (int j = 0; j < 10; j++)
                    {
                        UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)j).SignedAndResolved(k).TestObject;
                        _userOperationPool.AddUserOperation(op);
                    }
                });

                _userOperationPool.GetUserOperations().Should().HaveCount(TestItem.PrivateKeys.Length * 10);
                UserOperation[] opsForFirstTask = _userOperationPool.GetUserOperations().Where(o => o.Nonce == 8).ToArray();
                UserOperation[] opsForSecondTask = _userOperationPool.GetUserOperations().Where(o => o.Nonce == 6).ToArray();
                UserOperation[] opsForThirdTask = _userOperationPool.GetUserOperations().Where(o => o.Nonce == 7).ToArray();
                opsForFirstTask.Should().HaveCount(TestItem.PrivateKeys.Length);
                Task firstTask = Task.Run(() => DeleteOpsFromPool(opsForFirstTask));
                Task secondTask = Task.Run(() => DeleteOpsFromPool(opsForSecondTask));
                Task thirdTask = Task.Run(() => DeleteOpsFromPool(opsForThirdTask));
                await Task.WhenAll(firstTask, secondTask, thirdTask);
                _userOperationPool.GetUserOperations().Should().HaveCount(TestItem.PrivateKeys.Length * 7);
            }
            

            void DeleteOpsFromPool(UserOperation[] ops)
            {
                foreach (UserOperation op in ops)
                {
                    _userOperationPool.RemoveUserOperation(op.Hash);
                }
            }
        }
        
        [Test]
        public void should_remove_user_operations_from_pool_when_included_in_block()
        {
            int capacity = 256;
            int expectedTransactions = 100;
            _userOperationPool = GenerateUserOperationPool(capacity);
            _signer.Address.Returns(Address.SystemUser);
            Address senderAddress = _signer.Address;
            
            for (int i = 0; i < expectedTransactions; i++)
            {
                UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)i).SignedAndResolved().TestObject;
                _userOperationPool.AddUserOperation(op);
            }
            _userOperationPool.GetUserOperations().Should().HaveCount(expectedTransactions);

            UserOperation[] uops = _userOperationPool.GetUserOperations().ToArray();
            LogEntry[] logs = new LogEntry[uops.Length];
            for (int i = 0; i < uops.Length; i++)
            {
                Keccak[] topics = new[] {_userOperationEventTopic, new Keccak(string.Concat("0x000000000000000000000000", senderAddress.ToString(false, false))), new Keccak(string.Concat("0x000000000000000000000000", uops[i].Paymaster.Bytes.ToHexString()))};
                UInt256 nonce = (UInt256)i;
                logs[i] = new LogEntry(senderAddress, nonce.ToBigEndian(), topics);
            }
            TxReceipt receipt = new()
            {
                Recipient = new Address(_entryPointContractAddress),
                Sender = senderAddress,
                Logs = logs
            };

            Block block = Nethermind.Core.Test.Builders.Build.A.Block.TestObject;
            _receiptFinder.Get(block).Returns(new[]{receipt});
            BlockReplacementEventArgs blockReplacementEventArgs = new(block, null);
            
            ManualResetEvent manualResetEvent = new(false);
            _userOperationPool.RemoveUserOperation(Arg.Do<Keccak>(o => manualResetEvent.Set()));
            _blockTree.BlockAddedToMain += Raise.EventWith(new object(), blockReplacementEventArgs);
            manualResetEvent.WaitOne(500);
            
            _userOperationPool.GetUserOperations().Should().HaveCount(0);
        }

        [Test]
        public void should_add_peers()
        {
            _userOperationPool = GenerateUserOperationPool(100);
            IList<IUserOperationPoolPeer> peers = GetPeers();

            foreach (IUserOperationPoolPeer peer in peers)
            {
                _userOperationPool.AddPeer(peer);
            }
        }

        [Test]
        public void should_delete_peers()
        {
            _userOperationPool = GenerateUserOperationPool(100);
            IList<IUserOperationPoolPeer> peers = GetPeers();

            foreach (IUserOperationPoolPeer peer in peers)
            {
                _userOperationPool.AddPeer(peer);
            }

            foreach (IUserOperationPoolPeer peer in peers)
            {
                _userOperationPool.RemovePeer(peer.Id);
            }
        }
        
        [Test]
        public void should_notify_added_peer_about_ops_in_UOpPool()
        {
            _userOperationPool = GenerateUserOperationPool();
            UserOperation op = Build.A.UserOperation.SignedAndResolved().TestObject;
            _userOperationPool.AddUserOperation(op);
            IUserOperationPoolPeer uopPoolPeer = Substitute.For<IUserOperationPoolPeer>();
            uopPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _userOperationPool.AddPeer(uopPoolPeer);
            uopPoolPeer.Received().SendNewUserOperations(Arg.Any<IEnumerable<UserOperation>>());
        }
        
        [Test]
        public void should_send_to_peers_newly_added_uop()
        {
            _userOperationPool = GenerateUserOperationPool();
            IUserOperationPoolPeer uopPoolPeer = Substitute.For<IUserOperationPoolPeer>();
            uopPoolPeer.Id.Returns(TestItem.PublicKeyA);
            _userOperationPool.AddPeer(uopPoolPeer);
            UserOperation op = Build.A.UserOperation.WithSender(TestItem.AddressA).SignedAndResolved().TestObject;
            _userOperationPool.AddUserOperation(op);
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
            _userOperationPool = GenerateUserOperationPool(1);

            _userOperationPool.AddUserOperation(userOperation);
            UserOperation[] userOperations = _userOperationPool.GetUserOperations().ToArray();
            userOperations.Length.Should().Be(0);
        }

        private UserOperationPool GenerateUserOperationPool(int capacity = 10)
        {
            UserOperationSortedPool userOperationSortedPool =
                new(capacity, CompareUserOperationsByDecreasingGasPrice.Default, LimboLogs.Instance);

            _stateProvider.GetBalance(Arg.Any<Address>()).Returns(1.Ether());
            _stateProvider.AccountExists(Arg.Any<Address>()).Returns(true);
            _stateProvider.IsContract(Arg.Any<Address>()).Returns(true);

            _stateProvider.GetBalance(_notAnAddress).Returns(0.Ether());
            _stateProvider.AccountExists(_notAnAddress).Returns(false);
            _stateProvider.IsContract(_notAnAddress).Returns(false);

            _simulator.Simulate(Arg.Any<UserOperation>(), Arg.Any<BlockHeader>())
                .ReturnsForAnyArgs(x => Task.FromResult(ResultWrapper<Keccak>.Success(Keccak.Zero)));

            _blockTree.Head.Returns(Core.Test.Builders.Build.A.Block.TestObject);

            IAccountAbstractionConfig config = Substitute.For<IAccountAbstractionConfig>();
            config.EntryPointContractAddress.Returns(_entryPointContractAddress);
            config.UserOperationPoolSize.Returns(capacity);
            
            IPaymasterThrottler paymasterThrottler = Substitute.For<PaymasterThrottler>();
            
            return new UserOperationPool(
                config,
                _blockTree,
                new Address(_entryPointContractAddress), 
                NullLogger.Instance, 
                paymasterThrottler, 
                _receiptFinder, 
                _signer, 
                _stateProvider, 
                Substitute.For<ITimestamper>(), 
                _simulator, 
                userOperationSortedPool);
        }
    }
}
