// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.P2P;

/// <summary>Holds the node's own eth2 <c>MetaData</c>, served over the <c>metadata</c> and <c>ping</c> protocols.</summary>
/// <remarks>
/// The node does not attest, so the attestation subnet bitfields stay empty. The custody group count
/// uses the same configuration as <see cref="Discovery.LocalCustody"/>.
/// </remarks>
public class LocalMetadataSource(IBeaconChainConfig? config = null)
{
    public MetaDataV3 Current { get; set; } = new()
    {
        SeqNumber = 1,
        Attnets = new BitArray(64),
        Syncnets = new BitArray(4),
        CustodyGroupCount = config?.CustodyGroupCount ?? Eip7594DasConstants.CustodyRequirement,
    };
}
