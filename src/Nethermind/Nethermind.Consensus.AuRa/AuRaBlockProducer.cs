// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaBlockProducer(ITxSource txSource,
        IBlockchainProcessor processor,
        IWorldState stateProvider,
        ISealer sealer,
        IBlockTree blockTree,
        ITimestamper timestamper,
        IAuRaStepCalculator auRaStepCalculator,
        IReportingValidator reportingValidator,
        IAuraConfig config,
        IGasLimitCalculator gasLimitCalculator,
        ISpecProvider specProvider,
        ILogManager logManager,
        IBlocksConfig blocksConfig) : BlockProducerBase(
            txSource,
            processor,
            sealer,
            blockTree,
            stateProvider,
            gasLimitCalculator,
            timestamper,
            specProvider,
            logManager,
            new AuraDifficultyCalculator(auRaStepCalculator),
            blocksConfig)
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator = auRaStepCalculator ?? throw new ArgumentNullException(nameof(auRaStepCalculator));
        private readonly IReportingValidator _reportingValidator = reportingValidator ?? throw new ArgumentNullException(nameof(reportingValidator));
        private readonly IAuraConfig _config = config ?? throw new ArgumentNullException(nameof(config));

        protected override BlockToProduce PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null, IBlockProducer.Flags flags = IBlockProducer.Flags.None)
        {
            BlockToProduce block = base.PrepareBlock(parent, payloadAttributes, flags);
            // Upgrade to an AuRa-typed header in place when needed so the step can be stamped;
            // signature is filled in later by AuRaSealer. Skip the WithReplacedHeader churn when
            // the header is already AuRa-typed (the common case once chainspec/decoder produce them).
            if (block.Header is AuRaBlockHeader aura)
            {
                aura.AuRaStep = _auRaStepCalculator.CurrentStep;
                return block;
            }

            AuRaBlockHeader upgraded = UpgradeHeaderToAuRa(block.Header);
            upgraded.AuRaStep = _auRaStepCalculator.CurrentStep;
            return (BlockToProduce)block.WithReplacedHeader(upgraded);
        }

        protected override Block? ProcessPreparedBlock(Block block, IBlockTracer? blockTracer, CancellationToken token)
        {
            Block? processedBlock = base.ProcessPreparedBlock(block, blockTracer, token);

            if (processedBlock is not null)
            {
                // If force sealing is not on and we didn't pick up any transactions, then we should skip producing block
                if (processedBlock.Transactions.Length == 0)
                {
                    if (_config.ForceSealing)
                    {
                        if (Logger.IsDebug) Logger.Debug($"Force sealing block {block.Number} without transactions.");
                    }
                    else
                    {
                        if (Logger.IsDebug) Logger.Debug($"Skip seal block {block.Number}, no transactions pending.");
                        return null;
                    }
                }
            }

            return processedBlock;
        }

        protected override Task<Block> SealBlock(Block block, BlockHeader parent, CancellationToken token)
        {
            // if (block.Number < EmptyStepsTransition)
            _reportingValidator.TryReportSkipped(block.Header, parent);
            return base.SealBlock(block, parent, token);
        }

        /// <summary>
        /// Identity-preserving upgrade: returns the same instance when already AuRa, otherwise
        /// clones a base <see cref="BlockHeader"/> into an <see cref="AuRaBlockHeader"/>. Used by
        /// <c>PrepareBlock</c> to stamp the step before the sealer produces the signature.
        /// </summary>
        private static AuRaBlockHeader UpgradeHeaderToAuRa(BlockHeader header)
        {
            if (header is AuRaBlockHeader aura) return aura;

            return new AuRaBlockHeader(header.ParentHash, header.UnclesHash, header.Beneficiary,
                header.Difficulty, header.Number, header.GasLimit, header.Timestamp, header.ExtraData)
            {
                Author = header.Author,
                StateRoot = header.StateRoot,
                TxRoot = header.TxRoot,
                ReceiptsRoot = header.ReceiptsRoot,
                Bloom = header.Bloom,
                GasUsed = header.GasUsed,
                MixHash = header.MixHash,
                Nonce = header.Nonce,
                Hash = header.Hash,
                TotalDifficulty = header.TotalDifficulty,
                BaseFeePerGas = header.BaseFeePerGas,
                WithdrawalsRoot = header.WithdrawalsRoot,
                ParentBeaconBlockRoot = header.ParentBeaconBlockRoot,
                RequestsHash = header.RequestsHash,
                BlockAccessListHash = header.BlockAccessListHash,
                BlobGasUsed = header.BlobGasUsed,
                ExcessBlobGas = header.ExcessBlobGas,
                SlotNumber = header.SlotNumber,
                IsPostMerge = header.IsPostMerge,
            };
        }
    }
}
