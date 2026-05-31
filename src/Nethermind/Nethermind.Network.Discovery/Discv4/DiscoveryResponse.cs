// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Discv4;

public readonly struct DiscoveryResponse<T>
{
    private DiscoveryResponse(T value)
    {
        HasResponse = true;
        Value = value;
    }

    public bool HasResponse { get; }

    public T Value { get; }

    public static DiscoveryResponse<T> None => default;

    public static DiscoveryResponse<T> From(T value) => new(value);
}
