// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.AccountAbstraction
{
    public class AccountAbstractionConfig : IAccountAbstractionConfig
    {
        public bool Enabled { get; set; }
        public int AaPriorityPeersMaxCount { get; set; } = 20;
        public int UserOperationPoolSize { get; set; } = 200;
        public int MaximumUserOperationPerSender { get; set; } = 1;
        public string EntryPointContractAddresses { get; set; } = "";
        public UInt256 MinimumGasPrice { get; set; } = 1;
        public string WhitelistedPaymasters { get; set; } = "";
        public string FlashbotsEndpoint { get; set; } = "https://relay.flashbots.net/";
    }
}
