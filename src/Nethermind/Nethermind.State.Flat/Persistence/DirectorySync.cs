// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.Persistence;

/// <summary>Forces a directory's entries to durable storage.</summary>
/// <remarks>
/// Syncing a file does not sync the directory entry naming it, so a file created and fsynced can still be absent
/// after a power loss. Callers that later look the file up by name - the SST ingest redo marker lists its staged
/// files by path - need the directory synced too.
/// </remarks>
internal static class DirectorySync
{
    private const int O_RDONLY = 0;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    /// <summary>
    /// <c>fsync(2)</c> on <paramref name="path"/> itself. No-op off Linux, matching the rest of the flat DB's
    /// durability syscalls - the production target is Linux, and elsewhere this only weakens a crash window.
    /// </summary>
    /// <exception cref="IOException">The directory could not be opened or synced.</exception>
    internal static void Fsync(string path)
    {
        if (!OperatingSystem.IsLinux()) return;

        int fd = Open(path, O_RDONLY);
        if (fd < 0) throw new IOException($"open of directory '{path}' failed: errno {Marshal.GetLastPInvokeError()}");

        try
        {
            if (Fsync(fd) != 0)
                throw new IOException($"fsync of directory '{path}' failed: errno {Marshal.GetLastPInvokeError()}");
        }
        finally
        {
            Close(fd);
        }
    }
}
