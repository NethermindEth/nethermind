// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using System.Text;

namespace Nethermind.Zkvm.Abstractions;

/// <summary>
/// Provides <see href="https://github.com/eth-act/zkvm-standards/tree/main/standards/io-interface">zkVM I/O</see>.
/// </summary>
public static unsafe partial class IO
{
    /// <summary>
    /// Prints the given string followed by a newline to the standard output.
    /// </summary>
    /// <param name="value">The string to print.</param>
    public static void PrintLine(ReadOnlySpan<char> value)
    {
        // TODO: Remove when added to the zkVM standards
        int len = Encoding.UTF8.GetByteCount(value) + 1;

        Span<byte> buffer = len <= 512 ? stackalloc byte[len] : new byte[len];

        Encoding.UTF8.GetBytes(value, buffer);

        buffer[len - 1] = (byte)'\n';

        fixed (byte* ptr = &MemoryMarshal.GetReference(buffer))
            sys_write(1u, ptr, (nuint)len);
    }

    /// <summary>
    /// Reads raw bytes from the input stream.
    /// </summary>
    /// <returns>The data read.</returns>
    public static ReadOnlySpan<byte> ReadInput()
    {
        byte* ptr = null;

        read_input(&ptr, out nuint size);

        return size == nuint.Zero ? [] : new ReadOnlySpan<byte>(ptr, checked((int)size));
    }

    /// <summary>
    /// Writes raw bytes to public outputs.
    /// </summary>
    /// <param name="output">The data buffer.</param>
    public static void WriteOutput(ReadOnlySpan<byte> output) => write_output(output, (nuint)output.Length);
}
