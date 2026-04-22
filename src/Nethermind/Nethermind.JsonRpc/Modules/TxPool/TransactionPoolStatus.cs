// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool;

public class TxPoolStatus(TxPoolInfo info)
{
    public long Pending { get; set; } = info.Pending.Sum(static t => t.Value.Count);
    public long Queued { get; set; } = info.Queued.Sum(static t => t.Value.Count);
}
