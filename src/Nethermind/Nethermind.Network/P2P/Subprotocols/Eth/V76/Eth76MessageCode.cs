// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76;

/// <summary>EIP-8518 <c>eth/76</c> proof wrapper messages, the next IDs after <c>eth/74</c>'s <c>DependencyProofs</c> (<c>0x17</c>).</summary>
public static class Eth76MessageCode
{
    public const int NewProofWrapperHashes = 0x18;
    public const int GetProofWrappers = 0x19;
    public const int ProofWrappers = 0x1a;
    public const int GetProofWrapperManifests = 0x1b;
    public const int ProofWrapperManifests = 0x1c;
}
