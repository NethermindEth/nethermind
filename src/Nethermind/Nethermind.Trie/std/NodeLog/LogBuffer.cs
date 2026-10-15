// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Trie.NodeLog;

internal static class LogBuffer
{
    private const int HugePageBytes = 2 * 1024 * 1024;
    private const int MadvHugePage = 14;

    [DllImport("libc", EntryPoint = "madvise")]
    private static extern unsafe int Madvise(void* address, nuint length, int advice);

    public static unsafe byte[] Allocate(int length)
    {
        if (!OperatingSystem.IsLinux() || length < HugePageBytes) return new byte[length];

        // Permanent pinning keeps the advised mapping valid across compacting collections.
        byte[] buffer = GC.AllocateUninitializedArray<byte>(length, pinned: true);
        fixed (byte* data = buffer)
        {
            // Only full pages owned by the array are advised, excluding its header and neighbouring objects.
            nuint pageMask = (nuint)Environment.SystemPageSize - 1;
            nuint start = ((nuint)data + pageMask) & ~pageMask;
            nuint end = ((nuint)data + (nuint)length) & ~pageMask;
            Madvise((void*)start, end - start, MadvHugePage);
        }

        buffer.AsSpan().Clear();
        return buffer;
    }
}
