// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V74;

/// <summary>EIP-8512 <c>eth/74</c> dependency proof messages, the next IDs after <c>Cells</c> (<c>0x15</c>).</summary>
public static class Eth74MessageCode
{
    public const int GetDependencyProofs = 0x16;
    public const int DependencyProofs = 0x17;
}
