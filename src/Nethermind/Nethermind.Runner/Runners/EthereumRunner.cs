/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.KeyStore;
using Nethermind.Runner.Data;
using Nethermind.Store;

namespace Nethermind.Runner.Runners
{
    public class EthereumRunner : IEthereumRunner
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly IStateProvider _stateProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IDbProvider _dbProvider;
        private readonly ILogger _logger;
        private readonly IConfigurationProvider _configurationProvider;

        public EthereumRunner(IJsonSerializer jsonSerializer, IBlockchainProcessor blockchainProcessor, IBlockTree blockTree, IStateProvider stateProvider, IDbProvider dbProvider, ILogger logger, IConfigurationProvider configurationProvider, ISpecProvider specProvider)
        {
            _jsonSerializer = jsonSerializer;
            _blockchainProcessor = blockchainProcessor;
            _blockTree = blockTree;
            _stateProvider = stateProvider;
            _dbProvider = dbProvider;
            _logger = logger;
            _configurationProvider = configurationProvider;
            _specProvider = specProvider;
        }

        public void Start(InitParams initParams)
        {
            _logger.Log("Initializing Ethereum");
            _blockchainProcessor.Start();
            InitializeGenesis(initParams.GenesisFilePath);
            _logger.Log("Ethereum initialization completed");
        }

        public void Stop()
        {
        }

        private void InitializeGenesis(string genesisFile)
        {
            var genesisBlockRaw = File.ReadAllText(genesisFile);
            var blockJson = _jsonSerializer.Deserialize<TestGenesisJson>(genesisBlockRaw);
            var stateRoot = InitializeAccounts(blockJson.Alloc);
            var block = Convert(blockJson, stateRoot);
            _blockTree.AddBlock(block);
        }

        private static Block Convert(TestGenesisJson headerJson, Keccak stateRoot)
        {
            if (headerJson == null)
            {
                return null;
            }

            var header = new BlockHeader(
                new Keccak(headerJson.ParentHash),
                Keccak.OfAnEmptySequenceRlp,
                new Address(headerJson.Coinbase),
                Hex.ToBytes(headerJson.Difficulty).ToUnsignedBigInteger(),
                0,
                (long)Hex.ToBytes(headerJson.GasLimit).ToUnsignedBigInteger(),
                Hex.ToBytes(headerJson.Timestamp).ToUnsignedBigInteger(),
                Hex.ToBytes(headerJson.ExtraData)
            )
            {
                Bloom = Bloom.Empty,
                MixHash = new Keccak(headerJson.MixHash),
                Nonce = (ulong)Hex.ToBytes(headerJson.Nonce).ToUnsignedBigInteger(),
                ReceiptsRoot = Keccak.EmptyTreeHash,
                StateRoot = Keccak.EmptyTreeHash,
                TransactionsRoot = Keccak.EmptyTreeHash
            };

            header.StateRoot = stateRoot;
            header.RecomputeHash();

            var block = new Block(header);
            return block;
            //0xbd008bffd224489523896ed37442e90b4a7a3218127dafdfed9d503d95e3e1f3
        }

        private Keccak InitializeAccounts(IDictionary<string, TestAccount> alloc)
        {
            foreach (var account in alloc)
            {
                _stateProvider.CreateAccount(new Address(new Hex(account.Key)), account.Value.Balance.StartsWith("0x")
                    ? new BigInteger(new Hex(account.Value.Balance)) : BigInteger.Parse(account.Value.Balance));
            }
            _stateProvider.Commit(_specProvider.GenesisSpec);
            _dbProvider.Commit(_specProvider.GenesisSpec);
            return _stateProvider.StateRoot;
        }
    }
}