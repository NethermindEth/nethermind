// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.PortfolioViewer.Plugin;

/// <summary>Configuration for the portfolio viewer plugin.</summary>
public interface IPortfolioViewerConfig : IConfig
{
    /// <summary>Whether to serve the portfolio viewer UI and its endpoints. Disabled by default.</summary>
    [ConfigItem(Description = "Whether to serve the portfolio viewer UI (balances + NFTs) at the `/portfolio` path of the JSON-RPC HTTP endpoint.", DefaultValue = "false")]
    bool Enabled { get; set; }

    /// <summary>Comma-separated localhost ports probed to discover sibling nodes for the multi-chain view.</summary>
    [ConfigItem(Description = "Comma-separated localhost ports probed to discover sibling Nethermind nodes on other chains for the multi-chain portfolio viewer.", DefaultValue = "8545,8546,8547,8548,8549,8550")]
    string SiblingProbePorts { get; set; }

    /// <summary>Whether to serve the IPFS gateway and pin endpoints, which forward to a local IPFS node. Disabled by default.</summary>
    [ConfigItem(Description = "Whether to serve the `/portfolio-ipfs` endpoints, which forward NFT media requests and pins to a local IPFS (Kubo) node.", DefaultValue = "false")]
    bool IpfsEnabled { get; set; }
}
