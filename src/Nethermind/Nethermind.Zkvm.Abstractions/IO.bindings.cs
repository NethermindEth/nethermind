// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Zkvm.Abstractions;

public static partial class IO
{
    [LibraryImport("__Internal")]
    private static unsafe partial void read_input(byte** buf_ptr, out nuint buf_size);

    [LibraryImport("__Internal")]
    private static partial void write_output(ReadOnlySpan<byte> output, nuint size);

    // TODO: Remove when added to the zkVM standards: https://github.com/eth-act/zkvm-standards/issues/21
    [LibraryImport("__Internal")]
    private static unsafe partial void sys_write(uint _fd, byte* write_ptr, nuint nbytes);
}
