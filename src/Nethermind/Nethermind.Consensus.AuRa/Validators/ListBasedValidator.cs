// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa.Validators
{
    public sealed class ListBasedValidator : AuRaValidatorBase
    {
        public ListBasedValidator(AuRaParameters.Validator validator, IValidSealerStrategy validSealerStrategy, IValidatorStore validatorStore, ILogManager logManager, long startBlockNumber, bool forSealing = false)
            : base(validSealerStrategy, validatorStore, logManager, startBlockNumber, forSealing)
        {
            if (validator is null) throw new ArgumentNullException(nameof(validator));

            Validators = validator.Addresses?.Length > 0
                ? validator.Addresses
                : throw new ArgumentException("Empty validator Addresses.", nameof(validator.Addresses));

            InitValidatorStore();
        }
    }
}
