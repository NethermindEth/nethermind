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

    /// <summary>EIP-8408 indexed cell requests, which this client does not implement.</summary>
    public const byte Eth73 = 73;

    /// <summary>EIP-8512 dependency proof exchange for EIP-8288; built on <see cref="Eth72"/>, as EIP-8408's <c>eth/73</c> is not
    /// implemented, and activates with EIP-8288.</summary>
    public const byte Eth74 = 74;

    /// <summary>EIP-8437 direct-witness proof wrapper propagation; extends <see cref="Eth74"/>, a provisional version that
    /// activates with EIP-8288.</summary>
    public const byte Eth75 = 75;

    /// <summary>Aggregated proof wrapper propagation; extends <see cref="Eth75"/>, a provisional version that activates with
    /// EIP-8288.</summary>
    public const byte Eth76 = 76;
}
