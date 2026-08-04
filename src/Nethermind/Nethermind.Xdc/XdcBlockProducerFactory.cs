// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Xdc;

internal sealed class XdcBlockProducerFactory(
    StartXdcBlockProducer starter,
    IBlockTree blockTree,
    IXdcConsensusContext consensusContext,
    ISpecProvider specProvider,
    IEpochSwitchManager epochSwitchManager,
    IMasternodesCalculator masternodesCalculator,
    IVotesManager votesManager,
    ISigner signer,
    ITimeoutTimer timeoutTimer,
    ITimestamper timestamper,
    IBlockProcessingQueue processingQueue,
    IStateReader stateReader,
    ILogManager logManager)
    : IBlockProducerFactory, IBlockProducerRunnerFactory
{
    public IBlockProducer InitBlockProducer() => starter.BuildProducer();

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer) => new XdcHotStuff(
        blockTree,
        consensusContext,
        specProvider,
        blockProducer,
        epochSwitchManager,
        masternodesCalculator,
        votesManager,
        signer,
        timeoutTimer,
        timestamper,
        processingQueue,
        stateReader,
        logManager);
}
