// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Extensions;

public static partial class EvmWordExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReverseBytes(ulong value) => BinaryPrimitives.ReverseEndianness(value);
}
