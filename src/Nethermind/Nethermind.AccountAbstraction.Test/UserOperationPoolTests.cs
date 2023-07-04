// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime.Misc;
using FluentAssertions;
using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Org.BouncyCastle.Asn1.Cms;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public class UserOperationPoolTests
    {
        private IUserOperationPool _userOperationPool = Substitute.For<IUserOperationPool>();
        private IUserOperationSimulator _simulator = Substitute.For<IUserOperationSimulator>();
        private IBlockTree _blockTree = Substitute.For<IBlockTree>();
        private IReceiptFinder _receiptFinder = Substitute.For<IReceiptFinder>();
        private ILogFinder _logFinder = Substitute.For<ILogFinder>();
        private IWorldState _stateProvider = Substitute.For<IWorldState>();
        private ISpecProvider _specProvider = Substitute.For<ISpecProvider>();
        private readonly ISigner _signer = Substitute.For<ISigner>();
        private readonly Keccak _userOperationEventTopic = new("0x33fd4d1f25a5461bea901784a6571de6debc16cd0831932c22c6969cd73ba994");
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

            _simulator.Received().Simulate(op, Arg.Any<BlockHeader>(), Arg.Any<UInt256>(), Arg.Any<CancellationToken>());
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
            _simulator.Received().Simulate(op, Arg.Any<BlockHeader>(), Arg.Any<UInt256>(), Arg.Any<CancellationToken>());
            _userOperationPool.GetUserOperations().Count().Should().Be(1);
            _userOperationPool.GetUserOperations().Should().BeEquivalentTo(expectedOp);

            _userOperationPool.AddUserOperation(op2);
            _simulator.Received()
                .Simulate(op2, Arg.Any<BlockHeader>(), Arg.Any<UInt256>(), Arg.Any<CancellationToken>());
            _userOperationPool.GetUserOperations().Count().Should().Be(1);
            _userOperationPool.GetUserOperations().Should().BeEquivalentTo(expectedOp2);
        }

        [Test]
        public void should_add_user_operations_concurrently()
        {
            int capacity = 2048;
            _userOperationPool = GenerateUserOperationPool(capacity, capacity);

            Parallel.ForEach(TestItem.PrivateKeys, k =>
            {
                for (int i = 0; i < 100; i++)
                {
                    UserOperation op = Build.A.UserOperation.WithSender(k.Address).WithNonce((UInt256)i).SignedAndResolved(k).TestObject;
                    _userOperationPool.AddUserOperation(op);
                }
            });

            _userOperationPool.GetUserOperations().Count().Should().Be(capacity);
        }

        [Test]
        public async Task should_remove_user_operations_concurrently()
        {
            int capacity = 4096;
            _userOperationPool = GenerateUserOperationPool(capacity, capacity);

            int maxTryCount = 5;
            for (int i = 0; i < maxTryCount; ++i)
            {
                Parallel.ForEach(TestItem.PrivateKeys, k =>
                {
                    for (int j = 0; j < 10; j++)
                    {
                        UserOperation op = Build.A.UserOperation.WithSender(k.Address).WithNonce((UInt256)j).SignedAndResolved(k).TestObject;
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
                    _userOperationPool.RemoveUserOperation(op.RequestId!);
                }
            }
        }

        [Test]
        public void should_not_allow_more_than_max_capacity_per_sender_ops_from_same_sender()
        {
            _userOperationPool = GenerateUserOperationPool();
            for (int j = 0; j < 20; j++)
            {
                UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)j).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                _userOperationPool.AddUserOperation(op);
            }

            _userOperationPool.GetUserOperations().Count().Should().Be(10);
        }

        [Test]
        public void should_replace_op_with_higher_fee()
        {
            _userOperationPool = GenerateUserOperationPool();
            UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)0).WithMaxFeePerGas(10).WithMaxPriorityFeePerGas(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            _userOperationPool.AddUserOperation(op);

            _userOperationPool.GetUserOperations().Should().BeEquivalentTo(new[] { op });

            UserOperation higherGasPriceOp = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)0).WithMaxFeePerGas(20).WithMaxPriorityFeePerGas(20).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            _userOperationPool.AddUserOperation(higherGasPriceOp);

            _userOperationPool.GetUserOperations().Count().Should().Be(1);
            _userOperationPool.GetUserOperations().Should().BeEquivalentTo(new[] { higherGasPriceOp });
        }

        [Test]
        public void should_not_add_op_with_too_low_maxFeePerGas()
        {
            _userOperationPool = GenerateUserOperationPool();
            _blockTree.Head.Returns(Core.Test.Builders.Build.A.Block.WithBaseFeePerGas(1.Ether()).TestObject);

            UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)0).WithMaxFeePerGas(10).WithMaxPriorityFeePerGas(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            _userOperationPool.AddUserOperation(op);

            _userOperationPool.GetUserOperations().Should().BeNullOrEmpty();
        }

        [Test]
        public void should_increment_opsSeen_only_when_op_passes_baseFee()
        {
            TestPaymasterThrottler paymasterThrottler = new();
            Address paymasterAddress = Address.FromNumber(321312);

            _userOperationPool = GenerateUserOperationPool(10, 10, paymasterThrottler);
            _blockTree.Head.Returns(Core.Test.Builders.Build.A.Block.WithBaseFeePerGas(100).TestObject);

            UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithPaymaster(paymasterAddress).WithNonce((UInt256)0).WithMaxFeePerGas(80).WithMaxPriorityFeePerGas(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            _userOperationPool.AddUserOperation(op);

            _userOperationPool.GetUserOperations().Count().Should().Be(1);
            paymasterThrottler.GetOpsSeen(paymasterAddress).Should().Be(0);


            Block block = Nethermind.Core.Test.Builders.Build.A.Block.WithBaseFeePerGas(50).TestObject;
            _blockTree.Head.Returns(block);
            BlockEventArgs blockEventArgs = new(block);
            _blockTree.NewHeadBlock += Raise.EventWith(new object(), blockEventArgs);
            Thread.Sleep(TimeSpan.FromMilliseconds(100));

            _userOperationPool.GetUserOperations().Count().Should().Be(1);
            paymasterThrottler.GetOpsSeen(paymasterAddress).Should().Be(1);
        }

        [Test]
        public void should_not_add_op_with_higher_fee_that_does_not_replace_op_if_sender_has_too_many_ops()
        {
            _userOperationPool = GenerateUserOperationPool();

            for (int j = 0; j < 10; j++)
            {
                UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)j).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                _userOperationPool.AddUserOperation(op);
            }

            UserOperation higherGasPriceOp = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)10).WithMaxFeePerGas(20).WithMaxPriorityFeePerGas(20).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            _userOperationPool.AddUserOperation(higherGasPriceOp);

            _userOperationPool.GetUserOperations().Count().Should().Be(10);
            _userOperationPool.GetUserOperations().Should().NotContain(new[] { higherGasPriceOp });
        }

        [Test]
        public void should_replace_op_with_higher_fee_even_at_full_capacity()
        {
            _userOperationPool = GenerateUserOperationPool();

            IList<UserOperation> opsIncluded = new List<UserOperation>();

            for (int j = 0; j < 10; j++)
            {
                UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)j).WithMaxFeePerGas(10).WithMaxPriorityFeePerGas(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                opsIncluded.Add(op);
                _userOperationPool.AddUserOperation(op);
            }

            UserOperation higherGasPriceOp = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce(9).WithMaxFeePerGas(20).WithMaxPriorityFeePerGas(20).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            _userOperationPool.AddUserOperation(higherGasPriceOp);

            _userOperationPool.GetUserOperations().Count().Should().Be(10);
            _userOperationPool.GetUserOperations().Take(9).Should().BeEquivalentTo(opsIncluded.Take(9));
            _userOperationPool.GetUserOperations().Last().Should().BeEquivalentTo(higherGasPriceOp);

            UserOperation higherGasPriceOp2 = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce(3).WithMaxFeePerGas(20).WithMaxPriorityFeePerGas(20).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            _userOperationPool.AddUserOperation(higherGasPriceOp2);

            _userOperationPool.GetUserOperations().Count().Should().Be(10);
            _userOperationPool.GetUserOperations().ToArray()[3].Should().Be(higherGasPriceOp2);
            _userOperationPool.GetUserOperations().Should().NotContain(opsIncluded.ToArray()[3]);
        }

        [Test]
        public void should_not_replace_op_with_lower_fee_at_full_capacity()
        {
            _userOperationPool = GenerateUserOperationPool();

            IList<UserOperation> opsIncluded = new List<UserOperation>();

            for (int j = 0; j < 10; j++)
            {
                UserOperation op = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce((UInt256)j).WithMaxFeePerGas(10).WithMaxPriorityFeePerGas(10).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                opsIncluded.Add(op);
                _userOperationPool.AddUserOperation(op);
            }

            UserOperation lowerGasPriceOp = Build.A.UserOperation.WithSender(Address.SystemUser).WithNonce(9).WithMaxFeePerGas(5).WithMaxPriorityFeePerGas(5).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            _userOperationPool.AddUserOperation(lowerGasPriceOp);

            _userOperationPool.GetUserOperations().Count().Should().Be(10);
            _userOperationPool.GetUserOperations().Take(10).Should().BeEquivalentTo(opsIncluded.Take(10));
            _userOperationPool.GetUserOperations().Should().NotContain(lowerGasPriceOp);
        }

        [Test]
        public void should_remove_user_operations_from_pool_when_included_in_block()
        {
            int capacity = 256;
            int expectedTransactions = 1;
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
                Keccak[] topics = new[] { _userOperationEventTopic, new Keccak(string.Concat("0x000000000000000000000000", senderAddress.ToString(false, false))), new Keccak(string.Concat("0x000000000000000000000000", uops[i].Paymaster.Bytes.ToHexString())) };
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
            _logFinder.FindLogs(Arg.Any<LogFilter>()).Returns(new[]
            {
                new FilterLog(0, 0, receipt,
                    new LogEntry(new Address(_entryPointContractAddress),
                        Bytes.Zero32,
                        new[] {_userOperationEventTopic, uops[0].RequestId!, Keccak.Zero, Keccak.Zero}))
            });
            //_receiptFinder.Get(block).Returns(new[]{receipt});
            BlockEventArgs blockEventArgs = new(block);

            ManualResetEvent manualResetEvent = new(false);
            _userOperationPool.RemoveUserOperation(Arg.Do<Keccak>(o => manualResetEvent.Set()));
            _blockTree.NewHeadBlock += Raise.EventWith(new object(), blockEventArgs);
            manualResetEvent.WaitOne(500);

            _userOperationPool.GetUserOperations().Should().HaveCount(0);
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

        private UserOperationPool GenerateUserOperationPool(int capacity = 10, int perSenderCapacity = 10, IPaymasterThrottler? paymasterThrottler = null)
        {
            IAccountAbstractionConfig config = Substitute.For<IAccountAbstractionConfig>();
            config.EntryPointContractAddresses.Returns(_entryPointContractAddress);
            config.UserOperationPoolSize.Returns(capacity);
            config.MaximumUserOperationPerSender.Returns(perSenderCapacity);

            UserOperationSortedPool userOperationSortedPool =
                new(capacity, CompareUserOperationsByDecreasingGasPrice.Default, LimboLogs.Instance, config.MaximumUserOperationPerSender);

            _stateProvider.GetBalance(Arg.Any<Address>()).Returns(1.Ether());
            _stateProvider.AccountExists(Arg.Any<Address>()).Returns(true);
            _stateProvider.IsContract(Arg.Any<Address>()).Returns(true);

            _stateProvider.GetBalance(_notAnAddress).Returns(0.Ether());
            _stateProvider.AccountExists(_notAnAddress).Returns(false);
            _stateProvider.IsContract(_notAnAddress).Returns(false);

            _simulator.Simulate(Arg.Any<UserOperation>(), Arg.Any<BlockHeader>())
                .ReturnsForAnyArgs(x => ResultWrapper<Keccak>.Success(Keccak.Zero));

            _blockTree.Head.Returns(Core.Test.Builders.Build.A.Block.TestObject);

            paymasterThrottler ??= Substitute.For<IPaymasterThrottler>();

            IUserOperationBroadcaster userOperationBroadcaster = Substitute.For<IUserOperationBroadcaster>();

            return new UserOperationPool(
                config,
                _blockTree,
                new Address(_entryPointContractAddress),
                NullLogger.Instance,
                paymasterThrottler,
                _logFinder,
                _signer,
                _stateProvider,
                _specProvider,
                Substitute.For<ITimestamper>(),
                _simulator,
                userOperationSortedPool,
                userOperationBroadcaster,
                TestBlockchainIds.ChainId);
        }
    }
}
