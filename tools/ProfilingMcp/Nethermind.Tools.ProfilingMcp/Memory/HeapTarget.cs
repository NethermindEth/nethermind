// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Microsoft.Diagnostics.Runtime;

namespace Nethermind.Tools.ProfilingMcp.Memory;

/// <summary>
/// Opens the managed heap of a live process or a dump file with ClrMD.
/// </summary>
internal static class HeapTarget
{
    public const string Description = "A process ID of a running .NET process, or a path to a dump file created by write_dump.";

    /// <remarks>
    /// On Windows a live process is captured with a PSS snapshot, so it is suspended only while the snapshot is taken rather than for the whole heap walk.
    /// Elsewhere the process stays suspended (via ptrace) until analysis finishes.
    /// </remarks>
    public static DataTarget Open(string target)
    {
        if (int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
        {
            if (OperatingSystem.IsWindows())
                return DataTarget.CreateSnapshotAndAttach(pid);

            try
            {
                return DataTarget.AttachToProcess(pid, suspend: true);
            }
            catch (ClrDiagnosticsException e)
            {
                // Containers usually lack CAP_SYS_PTRACE; a dump is written by the runtime itself and needs no ptrace from us.
                throw new InvalidOperationException($"{e.Message} Use write_dump on the PID and pass the dump path as the target instead.", e);
            }
        }

        if (!File.Exists(target))
            throw new FileNotFoundException($"'{target}' is neither a PID nor an existing dump file.");

        return DataTarget.LoadDump(target);
    }

    public static ClrRuntime CreateRuntime(DataTarget dataTarget)
    {
        if (dataTarget.ClrVersions.Length == 0)
            throw new InvalidOperationException("No .NET runtime found in the target.");

        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        if (!runtime.Heap.CanWalkHeap)
            throw new InvalidOperationException("The heap cannot be walked right now (the process is probably in the middle of a GC). Try again.");

        return runtime;
    }
}
