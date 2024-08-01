// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Wallet
{
    public class WalletConfig : IWalletConfig
    {
        public int DevAccounts { get; set; } = 10;
    }
}
