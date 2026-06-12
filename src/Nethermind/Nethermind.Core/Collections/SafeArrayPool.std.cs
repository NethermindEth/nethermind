// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;

namespace Nethermind.Core.Collections;

#pragma warning disable NETH003 // Build variant: only one of SafeArrayPool.std.cs / SafeArrayPool.zkevm.cs is compiled per build
/// <summary>
/// Shared array pool. Standard execution delegates to <see cref="ArrayPool{T}.Shared"/>;
/// the zkVM build provides a single-threaded power-of-two bucket pool instead.
/// </summary>
public static class SafeArrayPool<T>
{
    public static readonly ArrayPool<T> Shared = ArrayPool<T>.Shared;
}
