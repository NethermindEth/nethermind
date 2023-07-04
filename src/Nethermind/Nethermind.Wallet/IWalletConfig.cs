// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Wallet
{
    public interface IWalletConfig : IConfig
    {
        [ConfigItem(DefaultValue = "10", Description = "Number of auto-generted dev accounts to work with. Dev accounts will have private keys from 00...01 to 00..n")]
        int DevAccounts { get; set; }
    }
}
