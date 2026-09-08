// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.TxPool;

public class TxPoolStatus(TxPoolCounts counts)
{
    public ulong Pending { get; set; } = (ulong)counts.Pending;
    public ulong Queued { get; set; } = (ulong)counts.Queued;
}
