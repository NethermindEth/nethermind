// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V74;

/// <summary>EIP-8498 <c>eth/74</c> dependency proof messages.</summary>
/// <remarks>IDs <c>0x16</c> to <c>0x18</c> stay with the EIP-8437 wrapper messages of <c>eth/73</c>.</remarks>
public static class Eth74MessageCode
{
    public const int GetDependencyProofs = 0x19;
    public const int DependencyProofs = 0x1a;
}
