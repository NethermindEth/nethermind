// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.P2P;

/// <summary>Holds the node's own eth2 <c>MetaData</c>, served over the <c>metadata</c> and <c>ping</c> protocols.</summary>
/// <remarks>
/// The node neither attests nor custodies extra data, so the subnet bitfields stay empty and the
/// custody group count is the Fulu <c>CUSTODY_REQUIREMENT</c> minimum.
/// </remarks>
public class LocalMetadataSource
{
    private const ulong CustodyRequirement = 4;

    public MetaDataV3 Current { get; set; } = new()
    {
        SeqNumber = 1,
        Attnets = new BitArray(64),
        Syncnets = new BitArray(4),
        CustodyGroupCount = CustodyRequirement,
    };
}
