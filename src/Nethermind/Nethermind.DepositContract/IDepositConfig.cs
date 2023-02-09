// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.DepositContract
{
    public interface IDepositConfig : IConfig
    {
        [ConfigItem(Description = "Address of the Eth2 deposit contract on the Eth1 network.", DefaultValue = "null")]
        string? DepositContractAddress { get; set; }
    }
}
