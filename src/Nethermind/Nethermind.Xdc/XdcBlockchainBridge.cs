// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Xdc;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Trie;
using Nethermind.TxPool;
using Nethermind.Config;
using Nethermind.Facade.Find;
using Nethermind.Facade.Filters;
using Nethermind.State;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.State.OverridableEnv;
using Nethermind.Blockchain.Headers;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Consensus.Stateless;
using Nethermind.Facade.Simulate;
using Nethermind.Core.Specs;
using Nethermind.Consensus;
using System;

namespace Nethermind.Xdc;

public class XdcBlockchainBridge : BlockchainBridge
{
    public XdcBlockchainBridge(
        IOverridableEnv<BlockchainBridge.BlockProcessingComponents> processingEnv,
        Lazy<ISimulateReadOnlyBlocksProcessingEnv> lazySimulateProcessingEnv,
        Lazy<IWitnessGeneratingBlockProcessingEnvFactory> witnessGeneratingBlockProcessingEnvFactory,
        IBlockTree blockTree,
        IStateReader stateReader,
        ITxPool txPool,
        IReceiptFinder receiptStorage,
        FilterStore filterStore,
        FilterManager filterManager,
        IEthereumEcdsa ecdsa,
        ITimestamper timestamper,
        ILogFinder logFinder,
        IBlockAccessListStore balStore,
        ISpecProvider specProvider,
        IBlocksConfig blocksConfig,
        IMiningConfig miningConfig)
        : base(processingEnv, lazySimulateProcessingEnv, witnessGeneratingBlockProcessingEnvFactory, blockTree, stateReader, txPool, receiptStorage, filterStore, filterManager, ecdsa, timestamper, logFinder, balStore, specProvider, blocksConfig, miningConfig)
    {
    }
    protected override BlockHeader CreateCallHeader(BlockHeader blockHeader, bool treatBlockHeaderAsParentBlock, IBlocksConfig blocksConfig, ITimestamper timestamper)
    {
        return treatBlockHeaderAsParentBlock
            ? new XdcBlockHeader(
                blockHeader.Hash!,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                UInt256.Zero,
                blockHeader.Number + 1,
                blockHeader.GasLimit,
                Math.Max(blockHeader.Timestamp + blocksConfig.SecondsPerSlot, timestamper.UnixTime.Seconds),
                [])
            : new XdcBlockHeader(
                blockHeader.ParentHash!,
                blockHeader.UnclesHash!,
                blockHeader.Beneficiary!,
                blockHeader.Difficulty,
                blockHeader.Number,
                blockHeader.GasLimit,
                blockHeader.Timestamp,
                blockHeader.ExtraData);
    }
}