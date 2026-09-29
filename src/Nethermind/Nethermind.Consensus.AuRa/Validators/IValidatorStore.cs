// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Validators
{
    public interface IValidatorStore
    {
        void SetValidators(ulong finalizingBlockNumber, Address[] validators);

        Address[] GetValidators(in ulong? blockNumber = null);
        ValidatorInfo GetValidatorsInfo(in ulong? blockNumber = null);

        PendingValidators PendingValidators { get; set; }
    }
}
