// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;

namespace Nethermind.Core;

public class HardwareInfo : IHardwareInfo
{
    public long AvailableMemoryBytes { get; }
    public int? MaxOpenFilesLimit { get; }

    public HardwareInfo()
    {
        // Note: Not the same as memory capacity. This take into account current system memory pressure such as
        // other process as well as OS level limit such as rlimit. Eh, its good enough.
        AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        MaxOpenFilesLimit = GetMaxOpenFilesLimit();
    }

    private static int? GetMaxOpenFilesLimit()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows doesn't have a per-process file descriptor limit like Unix systems
                // The limit is much higher (typically thousands), so we return null to indicate no specific limit
                return null;
            }
            else
            {
                // Unix-like systems (Linux, macOS, etc.)
                const int RLIMIT_NOFILE = 7; // Same value for both macOS and Linux

                RLimit limit = new();
                int result = getrlimit(RLIMIT_NOFILE, ref limit);

                if (result == 0)
                {
                    // Return the soft limit as it's what the process can actually use
                    // The soft limit can be raised up to the hard limit by the process
                    return (int)Math.Min(limit.rlim_cur, int.MaxValue);
                }
            }
        }
        catch
        {
            // If we can't detect the limit, return null to indicate unknown
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong rlim_cur; // Soft limit
        public ulong rlim_max; // Hard limit
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getrlimit(int resource, ref RLimit rlim);
}
