// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
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
            IList<long> blockGasLimitContractTransitions)
            : base(blockTree, sealValidator, specProvider, logManager)
        {
            _blockGasLimitContractTransitions = blockGasLimitContractTransitions ?? throw new ArgumentNullException(nameof(blockGasLimitContractTransitions));
        }

        protected override bool ValidateGasLimitRange(BlockHeader header, BlockHeader parent, IReleaseSpec spec) =>
            _blockGasLimitContractTransitions.TryGetForBlock(header.Number, out _) || base.ValidateGasLimitRange(header, parent, spec);
    }
}
