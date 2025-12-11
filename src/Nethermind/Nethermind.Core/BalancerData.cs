// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;

namespace Nethermind.Core;

public static class BalancerData
{
    public static readonly FrozenSet<AddressAsKey> Senders = [
        new AddressAsKey(new("0x506d1f9efe24f0d47853adca907eb8d89ae03207")),
        new AddressAsKey(new("0x491837cc85bbeab5f9b3110ad61f39d87f8ec618"))
    ];

    public static readonly FrozenSet<AddressAsKey> To = [
        new AddressAsKey(new("0x5e7fa86cfdd10de6129e53377335b78bb34eabd3")),
        new AddressAsKey(new("0x234490fa3cd6c899681c8e93ba88e97183a71fe4")),
        new AddressAsKey(new("0x49b5ce67b22b1d596842ca071ac3da93ee593e11")),
        new AddressAsKey(new("0x7b23c07a0bbbe652bf7069c9c4143a2c85132166")),
        new AddressAsKey(new("0x1bdc1febebf92bffab3a2e49c5cf3b7e35a9e81e"))
    ];
}
