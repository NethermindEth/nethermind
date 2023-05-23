// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Validators
{
    public interface IValidatorStore
    {
        void SetValidators(long finalizingBlockNumber, Address[] validators);

        Address[] GetValidators(in long? blockNumber = null);
        ValidatorInfo GetValidatorsInfo(in long? blockNumber = null);

        PendingValidators PendingValidators { get; set; }
    }
}
