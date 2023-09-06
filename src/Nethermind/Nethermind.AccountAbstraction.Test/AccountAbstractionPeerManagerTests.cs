// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Network;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public class AccountAbstractionPeerManagerTests
    {
        private IDictionary<Address, IUserOperationPool> _userOperationPools = new Dictionary<Address, IUserOperationPool>();
        private IUserOperationSimulator _simulator = Substitute.For<IUserOperationSimulator>();
        private IBlockTree _blockTree = Substitute.For<IBlockTree>();
        private ILogger _logger = Substitute.For<ILogger>();
        private ILogFinder _logFinder = Substitute.For<ILogFinder>();
        private IWorldState _stateProvider = Substitute.For<IWorldState>();
        private ISpecProvider _specProvider = Substitute.For<ISpecProvider>();
        private readonly ISigner _signer = Substitute.For<ISigner>();
        private readonly string[] _entryPointContractAddress = { "0x8595dd9e0438640b5e1254f9df579ac12a86865f", "0x96cc609c8f5458fb8a7da4d94b678e38ebf3d04e" };
        private static Address _notAnAddress = new("0x373f2D08b1C195fF08B9AbEdE3C78575FAAC2aCf");

        [Test]
        public void should_add_peers()
        {
            UserOperationBroadcaster _broadcaster = new UserOperationBroadcaster(_logger);
            GenerateMultiplePools();
            AccountAbstractionPeerManager _peerManager =
                new AccountAbstractionPeerManager(_userOperationPools, _broadcaster, _logger);
            IList<IUserOperationPoolPeer> peers = GetPeers();
            foreach (IUserOperationPoolPeer peer in peers)
            {
                _peerManager.AddPeer(peer);
            }
        }

        [Test]
        public void should_delete_peers()
        {
            UserOperationBroadcaster _broadcaster = new UserOperationBroadcaster(_logger);
            GenerateMultiplePools();
            AccountAbstractionPeerManager _peerManager =
                new AccountAbstractionPeerManager(_userOperationPools, _broadcaster, _logger);
            IList<IUserOperationPoolPeer> peers = GetPeers();

            foreach (IUserOperationPoolPeer peer in peers)
            {
                _peerManager.AddPeer(peer);
            }

            foreach (IUserOperationPoolPeer peer in peers)
            {
                _peerManager.RemovePeer(peer.Id);
            }
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

        private void GenerateMultiplePools()
        {
            for (int i = 0; i < _entryPointContractAddress.Length; i++)
            {
                Address entryPoint = new Address(_entryPointContractAddress[i]);
                _userOperationPools[entryPoint] = GenerateUserOperationPool(entryPoint, 100);
            }
        }

        private UserOperationPool GenerateUserOperationPool(Address entryPoint, int capacity = 10, int perSenderCapacity = 10)
        {
            IAccountAbstractionConfig config = Substitute.For<IAccountAbstractionConfig>();
            // config.EntryPointContractAddresses.Returns(_entryPointContractAddress);
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

            IPaymasterThrottler paymasterThrottler = Substitute.For<PaymasterThrottler>();
            IUserOperationBroadcaster userOperationBroadcaster = Substitute.For<IUserOperationBroadcaster>();

            return new UserOperationPool(
                config,
                _blockTree,
                entryPoint,
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
