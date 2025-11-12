// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Nethermind.Optimism.Test;

public record Fork(string Name, ulong Timestamp)
{
    private static readonly IReadOnlyList<Fork> All = typeof(Spec)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f =>
            f is { IsLiteral: true, IsInitOnly: false } &&
            f.FieldType == typeof(ulong) &&
            f.Name.EndsWith("timestamp", StringComparison.OrdinalIgnoreCase))
        .Select(f => new Fork(f.Name[..^("timestamp".Length)], (ulong)f.GetRawConstantValue()!))
        .OrderBy(x => x.Timestamp)
        .ToArray();

    public static Fork? ActiveAt(ulong value) => All.FirstOrDefault(t => t.Timestamp >= value);
    public static Fork? StartingAt(ulong value) => All.FirstOrDefault(t => t.Timestamp == value);
}
