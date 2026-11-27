// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Contract.P2P;

public static class EthVersions
{
    public const byte Eth62 = 62;
    public const byte Eth63 = 63;
    public const byte Eth64 = 64;
    public const byte Eth65 = 65;
    public const byte Eth66 = 66;
    public const byte Eth67 = 67;
    public const byte Eth68 = 68;
    public const byte Eth69 = 69;
    public const byte Eth70 = 70;
    public const byte Eth71 = 71;
    public const byte Eth72 = 72;

    /// <summary>EIP-8512 dependency proof exchange; built on <see cref="Eth72"/>, as EIP-8408's <c>eth/73</c> is not
    /// implemented, and advertised once EIP-8141, which introduces the dependency proof, is scheduled.</summary>
    public const byte Eth74 = 74;
}
