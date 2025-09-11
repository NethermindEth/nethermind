// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing
{
    public class GenesisLoader(
        IGenesisBuilder genesisBuilder,
        IStateReader stateReader,
        IBlockTree blockTree,
        IWorldState worldState,
        IBlockchainProcessor blockchainProcessor,
        GenesisLoader.Config genesisConfig,
        ILogManager logManager
    )
    {
        // cant import IInitConfig here, so we use a config record.
        public record Config(Hash256? ExpectedGenesisHash, TimeSpan GenesisTimeout);

        ILogger _logger = logManager.GetClassLogger<GenesisLoader>();

        public void Load()
        {
            using var _ = worldState.BeginScope(IWorldState.PreGenesis);

            Block genesis = genesisBuilder.Build();

            ValidateGenesisHash(genesisConfig.ExpectedGenesisHash, genesis.Header);

            ManualResetEventSlim genesisProcessedEvent = new(false);

            bool wasInvalid = false;
            void OnInvalidBlock(object? sender, IBlockchainProcessor.InvalidBlockEventArgs args)
            {
                if (args.InvalidBlock.Number != 0) return;
                blockchainProcessor.InvalidBlock -= OnInvalidBlock;
                wasInvalid = true;
                genesisProcessedEvent.Set();
            }
            blockchainProcessor.InvalidBlock += OnInvalidBlock;

            void GenesisProcessed(object? sender, BlockEventArgs args)
            {
                blockTree.NewHeadBlock -= GenesisProcessed;
                genesisProcessedEvent.Set();
            }
            blockTree.NewHeadBlock += GenesisProcessed;

            blockTree.SuggestBlock(genesis);
            bool genesisLoaded = genesisProcessedEvent.Wait(genesisConfig.GenesisTimeout);
            if (!genesisLoaded)
            {
                throw new TimeoutException($"Genesis block was not processed after {genesisConfig.GenesisTimeout.TotalSeconds} seconds. If you are running custom chain with very big genesis file consider increasing {nameof(BlocksConfig)}.{nameof(IBlocksConfig.GenesisTimeoutMs)}.");
            }

            if (wasInvalid)
            {
                throw new InvalidBlockException(genesis, "Error while generating genesis block.");
            }
        }

        /// <summary>
        /// If <paramref name="expectedGenesisHash"/> is <value>null</value> then it means that we do not care about the genesis hash (e.g. in some quick testing of private chains)/>
        /// </summary>
        /// <param name="expectedGenesisHash"></param>
        private void ValidateGenesisHash(Hash256? expectedGenesisHash, BlockHeader genesis)
        {
            if (expectedGenesisHash is not null && genesis.Hash != expectedGenesisHash)
            {
                if (_logger.IsTrace) _logger.Trace(stateReader.DumpState(genesis.StateRoot!));
                if (_logger.IsWarn) _logger.Warn(genesis.ToString(BlockHeader.Format.Full));
                if (_logger.IsError) _logger.Error($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {genesis.Hash}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Info($"Genesis hash :  {genesis.Hash}");
            }

            ThisNodeInfo.AddInfo("Genesis hash :", $"{genesis.Hash}");
        }
    }
}
