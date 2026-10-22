// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Network.Enr;
using EnrForkId = Nethermind.Network.Enr.ForkId;

namespace Nethermind.Network;

public static class ForkInfoExtensions
{
    /// <summary>
    /// Checks the ENR <c>eth</c> fork ID when present; records without one are accepted.
    /// </summary>
    public static bool IsNodeRecordForkCompatible(this IForkInfo forkInfo, NodeRecord? record)
        => record?.GetValue<EnrForkId>(EnrContentKey.Eth) is not { } forkId
           || forkInfo.IsForkIdCompatible(new ForkId(BinaryPrimitives.ReadUInt32BigEndian(forkId.ForkHash), forkId.Next));
}
