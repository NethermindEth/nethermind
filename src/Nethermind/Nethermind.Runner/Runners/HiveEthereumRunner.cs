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
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.KeyStore;
using Nethermind.Runner.Data;
using Nethermind.Store;

namespace Nethermind.Runner.Runners
{
    public class HiveEthereumRunner : IEthereumRunner
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IBlockTree _blockTree;
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly IStateProvider _stateProvider;
        private readonly ISpecProvider _specProvider;
        private readonly IDbProvider _dbProvider;
        private readonly ILogger _logger;
        private readonly IConfigurationProvider _configurationProvider;

        public HiveEthereumRunner(IJsonSerializer jsonSerializer, IBlockchainProcessor blockchainProcessor, IBlockTree blockTree, IStateProvider stateProvider, IDbProvider dbProvider, ILogger logger, IConfigurationProvider configurationProvider, ISpecProvider specProvider)
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

        public Task Start(InitParams initParams)
        {
            _logger.Info("Initializing Ethereum");
            _blockchainProcessor.Start();
            InitializeKeys(initParams.KeysDir);
            InitializeGenesis(initParams.GenesisFilePath);
            InitializeChain(initParams.ChainFile);
            InitializeBlocks(initParams.BlocksDir);
            _logger.Info("Ethereum initialization completed");
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            await _blockchainProcessor.StopAsync();
        }

        private void InitializeChain(string chainFile)
        {
            if (!File.Exists(chainFile))
            {
                _logger.Info($"Chain file does not exist: {chainFile}, skipping");
                return;
            }

            var chainFileContent = File.ReadAllBytes(chainFile);

            var blocksRlps = OldRlp.ExtractRlpList(new Rlp(chainFileContent));
            foreach (var blockRlp in blocksRlps)
            {
                Block block = Rlp.Decode<Block>(blockRlp);
                ProcessBlock(block);
            }
        }

        private void InitializeBlocks(string blocksDir)
        {
            if (!Directory.Exists(blocksDir))
            {
                _logger.Info($"Blocks dir does not exist: {blocksDir}, skipping");
                return;
            }

            var files = Directory.GetFiles(blocksDir).OrderBy(x => x).ToArray();
            var blocks = files.Select(x => new { File = x, Block = DecodeBlock(x) }).OrderBy(x => x.Block.Header.Number).ToArray();
            foreach (var block in blocks)
            {
                _logger.Info($"Processing block file: {block.File}, blockNumber: {block.Block.Header.Number}");
                ProcessBlock(block.Block);
            }
        }

        private Block DecodeBlock(string file)
        {
            var fileContent = File.ReadAllBytes(file);
            var blockRlp = new Rlp(fileContent);
            Block block = Rlp.Decode<Block>(blockRlp);
            return block;
        }

        private void ProcessBlock(Block block)
        {
            try
            {
                _blockTree.SuggestBlock(block);
            }
            catch (InvalidBlockException e)
            {
                _logger.Error($"Invalid block: {block.Hash}, ignoring", e);
            }
        }

        private void InitializeKeys(string keysDir)
        {
            if (!Directory.Exists(keysDir))
            {
                _logger.Info($"Keys dir does not exist: {keysDir}, skipping");
                return;
            }

            var keyStoreDir = GetStoreDirectory();
            var files = Directory.GetFiles(keysDir);
            foreach (var file in files)
            {
                _logger.Info($"Processing key file: {file}");
                var fileContent = File.ReadAllText(file);
                var keyStoreItem = _jsonSerializer.Deserialize<KeyStoreItem>(fileContent);
                var filePath = Path.Combine(keyStoreDir, keyStoreItem.Address);
                File.WriteAllText(filePath, fileContent, _configurationProvider.KeyStoreEncoding);
            }
        }

        private void InitializeGenesis(string genesisFile)
        {
            var genesisBlockRaw = File.ReadAllText(genesisFile);
            var blockJson = _jsonSerializer.Deserialize<TestGenesisJson>(genesisBlockRaw);
            var stateRoot = InitializeAccounts(blockJson.Alloc);
            var block = Convert(blockJson, stateRoot);
            _blockTree.SuggestBlock(block);
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
                (long) Hex.ToBytes(headerJson.GasLimit).ToUnsignedBigInteger(),
                Hex.ToBytes(headerJson.Timestamp).ToUnsignedBigInteger(),
                Hex.ToBytes(headerJson.ExtraData)
            )
            {
                Bloom = Bloom.Empty,
                MixHash = new Keccak(headerJson.MixHash),
                Nonce = (ulong) Hex.ToBytes(headerJson.Nonce).ToUnsignedBigInteger(),               
                ReceiptsRoot = Keccak.EmptyTreeHash,
                StateRoot = Keccak.EmptyTreeHash,
                TransactionsRoot = Keccak.EmptyTreeHash
            };

            header.StateRoot = stateRoot;
            header.Hash = BlockHeader.CalculateHash(header);

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

        private string GetStoreDirectory()
        {
            var directory = _configurationProvider.KeyStoreDirectory;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return directory;
        }
    }
}