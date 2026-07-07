// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Network.Enr;
using EnrForkId = Nethermind.Network.Enr.ForkId;

namespace Nethermind.Network;

public static class ForkInfoExtensions
{
    /// <summary>
    /// Checks whether the EIP-2124 fork ID advertised in the "eth" entry of a discovered node's ENR
    /// is compatible with the local fork schedule.
    /// </summary>
    /// <remarks>
    /// Records without the "eth" entry are accepted since there is nothing to validate.
    /// See https://github.com/ethereum/devp2p/blob/master/enr-entries/eth.md.
    /// </remarks>
    public static bool IsNodeRecordForkCompatible(this IForkInfo forkInfo, NodeRecord? record)
        => record?.GetValue<EnrForkId>(EnrContentKey.Eth) is not { } forkId
           || forkInfo.IsForkIdCompatible(new ForkId(BinaryPrimitives.ReadUInt32BigEndian(forkId.ForkHash), forkId.Next));
}
