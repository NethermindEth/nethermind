// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Merge.Plugin.KeepAlive;

/// <summary>What the engine keep-alive keeps out of its idle states between Engine API calls.</summary>
[Flags]
public enum EngineKeepAliveMode
{
    /// <summary>No keep-alive.</summary>
    Off = 0,

    /// <summary>The database device: a 4 KiB read past the page cache on every tick (Linux only).</summary>
    Disk = 1,

    /// <summary>A CPU core: a busy spin of a few tens of microseconds on every tick.</summary>
    Cpu = 2,

    /// <summary>Both the database device and a CPU core.</summary>
    Both = Disk | Cpu,
}

/// <summary>How the disk keep-alive's reads reach the device.</summary>
public enum DiskKeepAliveReadMode
{
    /// <summary>No reads: the disk keep-alive is off, unsupported here or disabled after failures.</summary>
    None = 0,

    /// <summary><c>pread</c> on a file opened with <c>O_DIRECT</c>.</summary>
    Direct = 1,

    /// <summary><c>posix_fadvise(POSIX_FADV_DONTNEED)</c> on the file, then a buffered <c>pread</c>.</summary>
    DropCache = 2,
}
