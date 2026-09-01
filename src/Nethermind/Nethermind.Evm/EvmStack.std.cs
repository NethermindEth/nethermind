// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReverseBytes(ulong value) => BinaryPrimitives.ReverseEndianness(value);
}
