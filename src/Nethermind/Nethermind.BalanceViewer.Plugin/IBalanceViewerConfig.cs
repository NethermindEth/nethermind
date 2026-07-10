// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.BalanceViewer.Plugin;

public interface IBalanceViewerConfig : IConfig
{
    [ConfigItem(Description = "Whether to serve the balance viewer UI at the `/balances` path of the JSON-RPC HTTP endpoint.", DefaultValue = "true")]
    bool Enabled { get; set; }
}
