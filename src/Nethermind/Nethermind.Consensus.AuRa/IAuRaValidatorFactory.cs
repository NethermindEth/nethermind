// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa
{
    public interface IAuRaValidatorFactory
    {
        IAuRaValidator CreateValidatorProcessor(AuRaParameters.Validator validator, BlockHeader parentHeader, long? startBlock = null);
    }
}
