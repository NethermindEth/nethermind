//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Hive
{
    public class HiveRunner
    {
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;
        private readonly IConfigProvider _configurationProvider;
        private readonly IFileSystem _fileSystem;
        private readonly IBlockValidator _blockValidator;
        private readonly ITracer _tracer;
        private SemaphoreSlim _resetEvent;

        public HiveRunner(
            IBlockTree blockTree,
            IConfigProvider configurationProvider,
            ILogger logger,
            IFileSystem fileSystem,
            IBlockValidator blockValidator,
            ITracer tracer)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _configurationProvider =
                configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _blockValidator = blockValidator;
            _tracer = tracer;


            _resetEvent = new SemaphoreSlim(0);
        }

        public async Task Start(CancellationToken cancellationToken)
        {
            if (_logger.IsInfo) _logger.Info("HIVE initialization started");
            _blockTree.BlockAddedToMain += BlockTreeOnBlockAddedToMain;
            IHiveConfig hiveConfig = _configurationProvider.GetConfig<IHiveConfig>();

            ListEnvironmentVariables();
            await InitializeBlocks(hiveConfig.BlocksDir, cancellationToken);
            await InitializeChain(hiveConfig.ChainFile);

            _blockTree.BlockAddedToMain -= BlockTreeOnBlockAddedToMain;

            if (_logger.IsInfo) _logger.Info("HIVE initialization completed");
        }

        private void BlockTreeOnBlockAddedToMain(object? sender, BlockEventArgs e)
        {
            _logger.Info($"HIVE block added to main: {e.Block.ToString(Block.Format.Short)}");
            _resetEvent.Release(1);
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

            string[] variableNames =
            {
                "HIVE_CHAIN_ID", "HIVE_BOOTNODE", "HIVE_TESTNET", "HIVE_NODETYPE", "HIVE_FORK_HOMESTEAD",
                "HIVE_FORK_DAO_BLOCK", "HIVE_FORK_DAO_VOTE", "HIVE_FORK_TANGERINE", "HIVE_FORK_SPURIOUS",
                "HIVE_FORK_METROPOLIS", "HIVE_FORK_BYZANTIUM", "HIVE_FORK_CONSTANTINOPLE", "HIVE_FORK_PETERSBURG",
                "HIVE_MINER", "HIVE_MINER_EXTRA", "HIVE_FORK_BERLIN", "HIVE_FORK_LONDON"
            };
            foreach (string variableName in variableNames)
            {
                if (_logger.IsInfo) _logger.Info($"{variableName}: {Environment.GetEnvironmentVariable(variableName)}");
            }
        }

        public async Task StopAsync()
        {
            await Task.CompletedTask;
        }

        private async Task InitializeBlocks(string blocksDir, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(blocksDir))
            {
                if (_logger.IsInfo) _logger.Info($"HIVE Blocks dir does not exist: {blocksDir}, skipping");
                return;
            }

            if (_logger.IsInfo) _logger.Info($"HIVE Loading blocks from {blocksDir}");

            string[] files = Directory.GetFiles(blocksDir).OrderBy(x => x).ToArray();
            if (_logger.IsInfo) _logger.Info($"Loaded {files.Length} files with blocks to process.");

            foreach (string file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    Block block = DecodeBlock(file);
                    if (_logger.IsInfo)
                        _logger.Info(
                            $"HIVE Processing block file: {file} - {block.ToString(Block.Format.Short)}");
                    await ProcessBlock(block);
                }
                catch (RlpException e)
                {
                    if (_logger.IsError) _logger.Error($"HIVE Wrong block rlp.", e);
                }
            }
        }

        private async Task InitializeChain(string chainFile)
        {
            if (!_fileSystem.File.Exists(chainFile))
            {
                if (_logger.IsInfo) _logger.Info($"HIVE Chain file does not exist: {chainFile}, skipping");
                return;
            }

            byte[] chainFileContent = _fileSystem.File.ReadAllBytes(chainFile);
            RlpStream rlpStream = new RlpStream(chainFileContent);
            List<Block> blocks = new List<Block>();

            if (_logger.IsInfo) _logger.Info($"HIVE Loading blocks from {chainFile}");
            while (rlpStream.ReadNumberOfItemsRemaining() > 0)
            {
                rlpStream.PeekNextItem();
                Block block = Rlp.Decode<Block>(rlpStream);
                if (_logger.IsInfo)
                    _logger.Info($"HIVE Reading a chain.rlp block {block.ToString(Block.Format.Short)}");
                blocks.Add(block);
            }

            for (int i = 0; i < blocks.Count; i++)
            {
                Block block = blocks[i];
                if (_logger.IsInfo)
                    _logger.Info($"HIVE Processing a chain.rlp block {block.ToString(Block.Format.Short)}");
                await ProcessBlock(block);
            }
        }

        private Block DecodeBlock(string file)
        {
            byte[] fileContent = File.ReadAllBytes(file);
            if (_logger.IsInfo) _logger.Info(fileContent.ToHexString());
            Rlp blockRlp = new(fileContent);
            return Rlp.Decode<Block>(blockRlp);
        }

        private async Task WaitForBlockProcessing(SemaphoreSlim semaphore)
        {
            if (!await semaphore.WaitAsync(5000))
            {
                throw new InvalidOperationException();
            }
        }

        private async Task ProcessBlock(Block block)
        {
            try
            {
                if (!_blockValidator.ValidateSuggestedBlock(block))
                {
                    if (_logger.IsInfo) _logger.Info($"Invalid block {block}");
                    return;
                }

                AddBlockResult result = await _blockTree.SuggestBlockAsync(block);
                if (result != AddBlockResult.Added && result != AddBlockResult.AlreadyKnown)
                {
                    if (_logger.IsError)
                        _logger.Error($"Cannot add block {block} to the blockTree, add result {result}");
                    return;
                }

                try
                {
                    if (_tracer.Trace(block, NullBlockTracer.Instance) is null)
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"Failed to process block {block}", ex);
                    return;
                }
                
                if (_logger.IsInfo)
                    _logger.Info(
                        $"HIVE suggested {block.ToString(Block.Format.Short)}, now best suggested header {_blockTree.BestSuggestedHeader}, head {_blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}");
                
                await WaitForBlockProcessing(_resetEvent);
            }
            catch (Exception e)
            {
                _logger.Error($"HIVE Invalid block: {block.Hash}, ignoring. ", e);
            }
        }
    }
}
