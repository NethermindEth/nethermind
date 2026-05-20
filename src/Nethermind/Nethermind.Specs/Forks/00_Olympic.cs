// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Specs.Forks;

public class Olympic() : NamedReleaseSpec<Olympic>(null)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Olympic";
        spec.MaximumExtraDataSize = 32;
        spec.MaxCodeSize = long.MaxValue;
        spec.MinGasLimit = 5000;
        spec.GasLimitBoundDivisor = 0x0400;
        spec.BlockReward = new UInt256(5000000000000000000ul);
        spec.DifficultyBoundDivisor = 0x0800;
        spec.IsEip3607Enabled = true;
        spec.MaximumUncleCount = 2;
        spec.Eip1559TransitionBlock = long.MaxValue;
        spec.ValidateChainId = true;
        spec.ValidateReceipts = true;
        spec.MinHistoryRetentionEpochs = 82125;

        // The below addresses are added for all forks, but the given EIPs can be enabled at a specific timestamp or block.
        spec.Eip7251ContractAddress = Eip7251Constants.ConsolidationRequestPredeployAddress;
        spec.Eip7002ContractAddress = Eip7002Constants.WithdrawalRequestPredeployAddress;
        spec.DepositContractAddress = Eip6110Constants.MainnetDepositContractAddress;
        spec.Eip7934MaxRlpBlockSize = Eip7934Constants.DefaultMaxRlpBlockSize;
    }
}
