// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Era1;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.TxPool;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nethermind.Xdc;

public class StartXdcBlockProducer(
    INethermindApi nethermindApi,
    IEpochSwitchManager epochSwitchManager,
    ISnapshotManager snapshotManager,
    IXdcConsensusContext xdcConsensusContext,
    ISpecProvider specProvider,
    IBlocksConfig blocksConfig,
    IBlockTree blockTree,
    ISealer sealer,
    ITimestamper timestamper,
    IGasLimitCalculator gasLimitCalculator,
    IDifficultyCalculator difficultyCalculator,
    ILogManager logManager)
{

    public IBlockProducer BuildProducer()
    {
        ILogger logger = logManager.GetClassLogger();
        if (logger.IsInfo) logger.Info("Starting XDC block producer & sealer");

        IBlockProducerEnv env = nethermindApi.BlockProducerEnvFactory.Create();

        return new XdcBlockProducer(
            epochSwitchManager,
            snapshotManager,
            xdcConsensusContext,
            env.TxSource,
            env.ChainProcessor,
            sealer,
            blockTree,
            env.ReadOnlyStateProvider,
            gasLimitCalculator,
            timestamper,
            specProvider,
            logManager,
            difficultyCalculator,
            blocksConfig
            );
    }
}

