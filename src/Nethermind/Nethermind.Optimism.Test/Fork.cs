// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Nethermind.Optimism.Test;

public record Fork(string Name, ulong Timestamp)
{
    // Aggregates all fork timestamps and names
    public static readonly FrozenDictionary<ulong, Fork> At = typeof(Spec)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f =>
            f is { IsLiteral: true, IsInitOnly: false } &&
            f.FieldType == typeof(ulong) &&
            f.Name.EndsWith("timestamp", StringComparison.OrdinalIgnoreCase)
        )
        .Select(f => new Fork(f.Name[..^("timestamp".Length)], (ulong)f.GetRawConstantValue()!))
        .ToFrozenDictionary(f => f.Timestamp);

    public static readonly IReadOnlyList<Fork> AllAndNextToGenesis = At.Values
        .Select(f => f.Timestamp == Spec.GenesisTimestamp ? new("Genesis + 1", f.Timestamp + 1) : f)
        .ToArray();

    public override string ToString() => Name;
}
