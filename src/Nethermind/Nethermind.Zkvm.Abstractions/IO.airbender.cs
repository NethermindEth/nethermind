// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Zkvm.Abstractions;

public static unsafe partial class IO
{
    /// <summary>Reads the input as <see cref="ReadInput"/> does, and has libairbender prepare a trusted input as it reads it.</summary>
    /// <remarks>
    /// libairbender hashes the witness nodes and codes of a trusted input while it copies them and reads the division
    /// hints and the field hint tape after it only on this path. The native trie, <c>airbender_prehashed_nodes</c>,
    /// <c>airbender_prehashed_code</c> and the hints are empty after <see cref="ReadInput"/>. Only the Airbender guest
    /// links libairbender.
    /// </remarks>
    public static ReadOnlySpan<byte> ReadPrehashedInput()
    {
        byte* ptr = null;

        airbender_read_prehashed_input(&ptr, out nuint size);

        return size == nuint.Zero ? [] : new ReadOnlySpan<byte>(ptr, checked((int)size));
    }

    [LibraryImport("__Internal")]
    private static unsafe partial void airbender_read_prehashed_input(byte** buf_ptr, out nuint buf_size);
}
