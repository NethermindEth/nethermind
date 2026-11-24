// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V72;

/// <summary>
/// A token bucket that limits cell-serving work. It refills at a constant rate up to its capacity.
/// </summary>
/// <remarks>
/// Not thread-safe. The owner holds its own lock around every call.
/// </remarks>
internal struct CellServeTokenBucket(int capacity, int tokensPerSecond, DateTimeOffset now)
{
    private double _tokens = capacity;
    private DateTimeOffset _updatedAt = now;

    /// <summary>
    /// Refills the bucket for the time elapsed since the last call, then takes <paramref name="work"/> tokens if enough are available.
    /// </summary>
    /// <returns><see langword="true"/> if the tokens were taken; otherwise, <see langword="false"/>.</returns>
    public bool TryConsume(int work, DateTimeOffset now)
    {
        double elapsedSeconds = Math.Max(0, (now - _updatedAt).TotalSeconds);
        _tokens = Math.Min(capacity, _tokens + elapsedSeconds * tokensPerSecond);
        _updatedAt = now;
        if (_tokens < work)
        {
            return false;
        }

        _tokens -= work;
        return true;
    }

    /// <summary>
    /// Gives back <paramref name="work"/> tokens, up to the capacity.
    /// </summary>
    public void Refund(int work) => _tokens = Math.Min(capacity, _tokens + work);
}
