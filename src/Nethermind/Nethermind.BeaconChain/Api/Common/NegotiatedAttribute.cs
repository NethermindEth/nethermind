// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Marks a handler whose request's <c>Accept</c> header must admit JSON, or SSZ when <see cref="Ssz"/>, checked before its parameters are bound.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class NegotiatedAttribute : Attribute
{
    /// <summary>Whether the handler serves SSZ besides JSON.</summary>
    public bool Ssz { get; init; }
}
