// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool;

public class TxPoolStatus(TxPoolInfo info)
{
    public ulong Pending { get; set; } = (ulong)info.Pending.Sum(static t => (long)t.Value.Count);
    public ulong Queued { get; set; } = (ulong)info.Queued.Sum(static t => (long)t.Value.Count);
}
