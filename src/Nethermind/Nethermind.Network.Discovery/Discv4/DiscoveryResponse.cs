// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Discv4;

/// <summary>
/// The result of a discovery request: either the peer's response, or nothing when the request timed out.
/// </summary>
/// <remarks>
/// Replaces exception-based timeout signalling on the request hot path. A timed-out request yields
/// <see cref="None"/> instead of throwing; genuine cancellation still surfaces as an exception.
/// </remarks>
/// <typeparam name="T">The response message type.</typeparam>
public readonly struct DiscoveryResponse<T>
{
    private DiscoveryResponse(T value)
    {
        HasResponse = true;
        Value = value;
    }

    /// <summary>Whether a response was received. When <see langword="false"/> the request timed out.</summary>
    public bool HasResponse { get; }

    /// <summary>
    /// The response message. Only meaningful when <see cref="HasResponse"/> is <see langword="true"/>;
    /// otherwise it is <see langword="default"/>.
    /// </summary>
    public T Value { get; }

    /// <summary>A no-response result, representing a timed-out request.</summary>
    public static DiscoveryResponse<T> None => default;

    /// <summary>Creates a result carrying a received response.</summary>
    public static DiscoveryResponse<T> From(T value) => new(value);
}
