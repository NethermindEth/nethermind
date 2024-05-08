// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

/// <summary>
/// Mark a list that is owned by the containing class/struct and should be disposed together with the class.
/// Conventionally:
/// - If this is returned from a method, the method caller should dispose it.
/// - If this is passed to a method, the receiving object for the method should dispose it.
/// You give it to me, I own it. I give it to you, you now own it. You own it, you clean it up la...
///
/// TODO: One day, check if https://github.com/dotnet/roslyn-analyzers/issues/1617 has progressed.
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IOwnedReadOnlyList<T> : IReadOnlyList<T>, IDisposable
{
    ReadOnlySpan<T> AsSpan();
}

public static class OwnedReadOnlyListExtensions
{
    public static void DisposeRecursive<T>(this IOwnedReadOnlyList<T> list) where T : IDisposable
    {
        for (int i = 0; i < list.Count; i++)
        {
            list[i]?.Dispose();
        }

        list.Dispose();
    }
}
