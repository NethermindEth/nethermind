//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;

namespace Nethermind.Runner.Hive
{
    public class HiveRunner : IRunner
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly IConfigProvider _configurationProvider;

        public HiveRunner(IBlockTree blockTree,
            IJsonSerializer jsonSerializer,
            IConfigProvider configurationProvider,
            ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        }

        public Task Start(CancellationToken cancellationToken)
        {
            if(_logger.IsInfo) _logger.Info("HIVE initialization starting");
            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
            var hiveConfig = _configurationProvider.GetConfig<IHiveConfig>();

            ListEnvironmentVariables();
            InitializeBlocks(hiveConfig.BlocksDir, cancellationToken);

            _blockTree.NewHeadBlock -= BlockTreeOnNewHeadBlock;

            if(_logger.IsInfo) _logger.Info("HIVE initialization completed");
            return Task.CompletedTask;
        }

        private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            _logger.Info($"HIVE new head block {e.Block.ToString(Block.Format.Short)}");
        }

        private void ListEnvironmentVariables()
        {
// # This script assumes the following environment variables:
// #  - HIVE_BOOTNODE       enode URL of the remote bootstrap node
// #  - HIVE_NETWORK_ID     network ID number to use for the eth protocol
// #  - HIVE_CHAIN_ID     network ID number to use for the eth protocol
// #  - HIVE_TESTNET        whether testnet nonces (2^20) are needed
// #  - HIVE_NODETYPE       sync and pruning selector (archive, full, light)
// #  - HIVE_FORK_HOMESTEAD block number of the DAO hard-fork transition
// #  - HIVE_FORK_DAO_BLOCK block number of the DAO hard-fork transitionnsition
// #  - HIVE_FORK_DAO_VOTE  whether the node support (or opposes) the DAO fork
// #  - HIVE_FORK_TANGERINE block number of TangerineWhistle
// #  - HIVE_FORK_SPURIOUS  block number of SpuriousDragon
// #  - HIVE_FORK_BYZANTIUM block number for Byzantium transition
// #  - HIVE_FORK_CONSTANTINOPLE block number for Constantinople transition
// #  - HIVE_FORK_PETERSBURG  block number for ConstantinopleFix/PetersBurg transition
// #  - HIVE_MINER          address to credit with mining rewards (single thread)
// #  - HIVE_MINER_EXTRA    extra-data field to set for newly minted blocks
// #  - HIVE_SKIP_POW       If set, skip PoW verification during block import

            string[] variableNames = {"HIVE_CHAIN_ID", "HIVE_BOOTNODE", "HIVE_TESTNET", "HIVE_NODETYPE", "HIVE_FORK_HOMESTEAD", "HIVE_FORK_DAO_BLOCK", "HIVE_FORK_DAO_VOTE", "HIVE_FORK_TANGERINE", "HIVE_FORK_SPURIOUS", "HIVE_FORK_METROPOLIS", "HIVE_FORK_BYZANTIUM", "HIVE_FORK_CONSTANTINOPLE", "HIVE_FORK_PETERSBURG", "HIVE_MINER", "HIVE_MINER_EXTRA"};
            foreach (string variableName in variableNames)
            {
                if(_logger.IsInfo) _logger.Info($"{variableName}: {Environment.GetEnvironmentVariable(variableName)}");
            }
        }

        public async Task StopAsync()
        {
            await Task.CompletedTask;
        }

        private void InitializeBlocks(string blocksDir, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(blocksDir))
            {
                if (_logger.IsInfo) _logger.Info($"HIVE Blocks dir does not exist: {blocksDir}, skipping");
                return;
            }

            if (_logger.IsInfo) _logger.Info($"HIVE Loading blocks from {blocksDir}");

            var files = Directory.GetFiles(blocksDir).OrderBy(x => x).ToArray();
            var blocks = files.Select(x => new {File = x, Block = DecodeBlock(x)}).OrderBy(x => x.Block.Header.Number).ToArray();

            foreach (var block in blocks)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                
                if (_logger.IsInfo) _logger.Info($"HIVE Processing block file: {block.File} - {block.Block.ToString(Block.Format.Short)}");
                ProcessBlock(block.Block);
            }
        }

        private Block DecodeBlock(string file)
        {
            var fileContent = File.ReadAllBytes(file);
            var blockRlp = new Rlp(fileContent);

            return Rlp.Decode<Block>(blockRlp);
        }

        private void ProcessBlock(Block block)
        {
            try
            {
                _blockTree.SuggestBlock(block);

                if (_logger.IsInfo) _logger.Info($"HIVE suggested {block.ToString(Block.Format.Short)}, now best suggested header {_blockTree.BestSuggestedHeader}, head {_blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}");
            }
            catch (InvalidBlockException e)
            {
                _logger.Error($"HIVE Invalid block: {block.Hash}, ignoring", e);
            }
        }
    }
}