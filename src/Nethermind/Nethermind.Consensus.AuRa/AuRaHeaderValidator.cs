// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaHeaderValidator : HeaderValidator
    {
        private readonly IList<long> _blockGasLimitContractTransitions;

        public AuRaHeaderValidator(
            IBlockTree blockTree,
            ISealValidator sealValidator,
            ISpecProvider specProvider,
            ILogManager logManager,
            AuRaChainSpecEngineParameters param)
            : base(blockTree, sealValidator, specProvider, logManager)
        {
            long[] blockGasLimitContractTransitions = param.BlockGasLimitContractTransitions.Keys.ToArray();
            _blockGasLimitContractTransitions = blockGasLimitContractTransitions;
        }

        protected override bool ValidateGasLimitRange(BlockHeader header, BlockHeader parent, IReleaseSpec spec, ref string error)
        {
            long headerNumber = (long)header.Number;
            return _blockGasLimitContractTransitions.TryGetForBlock(headerNumber, out _) || base.ValidateGasLimitRange(header, parent, spec, ref error);
        }
    }
}
