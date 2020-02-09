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
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization.BeamSync
{
    public class BeamBlockchainProcessor : IBlockchainProcessor
    {
        private readonly IReadOnlyDbProvider _readOnlyDbProvider;
        private readonly IBlockValidator _blockValidator;
        private readonly IBlockDataRecoveryStep _recoveryStep;
        private readonly IRewardCalculator _rewardCalculator;
        private readonly ILogger _logger;

        private IBlockchainProcessor _blockchainProcessor;
        private IBlockchainProcessor _oneTimeProcessor;

        public BeamBlockchainProcessor(
            IReadOnlyDbProvider readOnlyDbProvider,
            IBlockTree blockTree,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlockValidator blockValidator,
            IBlockDataRecoveryStep recoveryStep,
            IRewardCalculator rewardCalculator,
            IBlockchainProcessor blockchainProcessor)
        {
            _readOnlyDbProvider = readOnlyDbProvider ?? throw new ArgumentNullException(nameof(readOnlyDbProvider));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculator = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
            _blockchainProcessor = blockchainProcessor ?? throw new ArgumentNullException(nameof(blockchainProcessor));
            ReadOnlyTxProcessingEnv txEnv = new ReadOnlyTxProcessingEnv(readOnlyDbProvider, new ReadOnlyBlockTree(blockTree), specProvider, logManager);
            ReadOnlyChainProcessingEnv env = new ReadOnlyChainProcessingEnv(txEnv, _blockValidator, _recoveryStep, _rewardCalculator, NullReceiptStorage.Instance, _readOnlyDbProvider, specProvider, logManager);
            _oneTimeProcessor = env.ChainProcessor;
            _logger = logManager.GetClassLogger();
        }
        
        public void Start()
        {
        }

        public Task StopAsync(bool processRemainingBlocks = false)
        {
            return Task.CompletedTask;
        }

        public Block Process(Block block, ProcessingOptions options, IBlockTracer tracer)
        {
            // we only want to trace the actual block
            try
            {
                Block processedBlock = _oneTimeProcessor.Process(block, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance);
                if (processedBlock == null)
                {
                    return null;
                }
            }
            catch (Exception e)
            {
                _logger.Error($"Block {block.ToString(Block.Format.Short)} coudl not be processed", e);
                return null;
            }

            
            // at this stage we are sure to have all the state available
            return _blockchainProcessor.Process(block, options, tracer);
            
            // process the block on the one time chain processor
            // if task is timing out shelve it and try next/
            // listen to incoming blocks with higher difficulty so that Tasks can be cancelled
            // ensure not leaving corrupted state
            // wrap the standard processor that will process actual blocks normally (when all the witness is collected
            
            // use prefetcher in the tx pool
            // use the same prefetcher here potentially?
            // prefetch code!
        }
    }
}