// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    // RISC-V has no byte-swap instruction, so the BCL's ReverseEndianness expands to a byte-at-a-time
    // shuffle. ZkEvmBitOperations.Bswap64 does it with three masked shift/or pairs on whole words.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReverseBytes(ulong value) => ZkEvmBitOperations.Bswap64(value);
}
