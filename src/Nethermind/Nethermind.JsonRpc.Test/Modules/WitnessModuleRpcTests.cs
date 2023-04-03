// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules.Witness;
using Nethermind.Logging;
using Nethermind.State.Witnesses;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class WitnessModuleTests
    {
        private const string GetOneWitnessHashResponse =
            "{\"jsonrpc\":\"2.0\",\"result\":[\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\"],\"id\":67}";
        private const string BlockNotFoundResponse =
            "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32001,\"message\":\"Block not found\"},\"id\":67}";
        private const string WitnessNotFoundResponse =
            "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32002,\"message\":\"Witness unavailable\"},\"id\":67}";

        private IBlockFinder _blockFinder = null!;

        private WitnessCollector _witnessRepository = null!;
        private WitnessRpcModule _witnessRpcModule = null!;

        private Block _block = null!;


        [SetUp]
        public void Setup()
        {
            _block = Build.A.Block
                .WithUncles(Build.A.BlockHeader.TestObject, Build.A.BlockHeader.TestObject).TestObject;

            _blockFinder = Substitute.For<IBlockTree>();
            _witnessRepository = new WitnessCollector(new MemDb(), LimboLogs.Instance);
            _witnessRpcModule = new WitnessRpcModule(_witnessRepository, _blockFinder);
        }

        [Test]
        public void GetOneWitnessHash()
        {
            _blockFinder.FindHeader((BlockParameter)null!).ReturnsForAnyArgs(_block.Header);
            _blockFinder.Head.Returns(_block);

            using IDisposable tracker = _witnessRepository.TrackOnThisThread();
            _witnessRepository.Add(_block.Hash!);
            _witnessRepository.Persist(_block.Hash!);

            string serialized = RpcTest.TestSerializedRequest<IWitnessRpcModule>(_witnessRpcModule, "get_witnesses", _block.CalculateHash().ToString());
            serialized.Should().Be(GetOneWitnessHashResponse);
        }

        [Test]
        public void BlockNotFound()
        {
            string serialized =
                RpcTest.TestSerializedRequest<IWitnessRpcModule>(_witnessRpcModule, "get_witnesses", "0x583");
            serialized.Should().Be(BlockNotFoundResponse);
        }

        [Test]
        public void WitnessNotFound()
        {
            _blockFinder.FindHeader((BlockParameter)null!).ReturnsForAnyArgs(_block.Header);
            _blockFinder.Head.Returns(_block);

            string serialized =
                RpcTest.TestSerializedRequest<IWitnessRpcModule>(_witnessRpcModule, "get_witnesses", "0x1");
            serialized.Should().Be(WitnessNotFoundResponse);

        }
    }
}
