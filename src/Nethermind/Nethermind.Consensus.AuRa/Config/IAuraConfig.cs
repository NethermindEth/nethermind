// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Consensus.AuRa.Config
{
    public interface IAuraConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then Nethermind if mining will seal empty blocks.", DefaultValue = "true")]
        bool ForceSealing { get; set; }

        [ConfigItem(Description = "If 'true' then you can run Nethermind only private chains. Do not use with existing Parity AuRa chains.", DefaultValue = "false")]
        bool AllowAuRaPrivateChains { get; set; }

        [ConfigItem(Description = "If 'true' then when using BlockGasLimitContractTransitions if the contract returns less than 2mln gas, then 2 mln gas is used.", DefaultValue = "false")]
        bool Minimum2MlnGasPerBlockWhenUsingBlockGasLimitContract { get; set; }

        [ConfigItem(Description = "If set then transaction priority contract is used when selecting transactions from transaction pool. " +
                                  "See more at https://github.com/poanetwork/posdao-contracts/blob/master/contracts/TxPriority.sol",
            DefaultValue = "null")]

        string TxPriorityContractAddress { get; set; }

        [ConfigItem(Description = "If set then transaction priority rules are used when selecting transactions from transaction pool. This has higher priority then on chain contract rules. " +
                                  "See more at contract details https://github.com/poanetwork/posdao-contracts/blob/master/contracts/TxPriority.sol",
            DefaultValue = "null")]
        string TxPriorityConfigFilePath { get; set; }

        [ConfigItem(Description = "Whether to enable shuttering.")]
        bool UseShutter { get; set; }
    }
}
