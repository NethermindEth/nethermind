// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Runner.Monitoring.TransactionPool;

namespace Nethermind.Runner.Monitoring;

internal class NethermindNodeData(long uptime)
{
    public long Uptime => uptime;
    public string Instance => ProductInfo.Instance;
    public string Network => ProductInfo.Network;
    public string SyncType => ProductInfo.SyncType;
    public string PruningMode => ProductInfo.PruningMode;
    public string Version => ProductInfo.Version;
    public string Commit => ProductInfo.Commit;
    public string Runtime => ProductInfo.Runtime;
}
