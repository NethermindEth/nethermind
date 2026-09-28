// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History;

/// <summary>One retention floor: either a configured point scope for exactly one address (<see cref="IsGeneral"/>
/// false, <see cref="Key"/> its 20-byte account key), or the all-keys fallback (<see cref="IsGeneral"/> true,
/// <see cref="Key"/> empty).</summary>
public readonly record struct ScopeFloor(byte[] Key, ulong Floor, bool IsGeneral);
