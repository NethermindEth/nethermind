// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.BalanceViewer.Plugin;

public interface IPortfolioConfig : IConfig
{
    [ConfigItem(Description = "Whether to serve the portfolio UI (balances + NFTs) at the `/portfolio` path of the JSON-RPC HTTP endpoint.", DefaultValue = "true")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Comma-separated localhost ports probed to discover sibling Nethermind nodes on other chains for the multi-chain portfolio view.", DefaultValue = "8545,8546,8547,8548,8549,8550")]
    string SiblingProbePorts { get; set; }
}
