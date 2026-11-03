// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Utilities;

namespace Nethermind.Tools.ProfilingMcp.Memory;

/// <summary>
/// Tells process IDs, read over EventPipe, from dump files, opened with ClrMD.
/// </summary>
internal static class HeapTarget
{
    public const string Description = "A process ID of a running .NET process, read over EventPipe, or a path to a dump file created by write_dump, read with ClrMD.";

    public static bool IsProcess(string target, out int pid) => int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out pid);

    /// <remarks>
    /// ClrMD loads the runtime's files at the paths the dump records. A container sidecar finds them under the target's root instead;
    /// without that, ClrMD falls back to the symbol server, which the sidecar has no network to reach.
    /// </remarks>
    public static DataTarget OpenDump(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"'{path}' is neither a PID nor an existing dump file.");

        return Environment.GetEnvironmentVariable(ContainerSidecar.NameVariable) is null
            ? DataTarget.LoadDump(path)
            : DataTarget.LoadDump(path, new DataTargetOptions { FileLocator = new TargetRootLocator(), SymbolPaths = [] });
    }

    public static ClrRuntime CreateRuntime(DataTarget dataTarget)
    {
        if (dataTarget.ClrVersions.Length == 0)
            throw new InvalidOperationException("No .NET runtime found in the target.");

        ClrInfo clr = dataTarget.ClrVersions[0];

        // ClrMD reads the runtime's version from the file at the recorded path, which a sidecar cannot see, so it reports 0.0.
        // The DAC shipped next to the target's runtime is the matching one, so the version check is skipped for it.
        string targetDac = $"{ContainerSidecar.TargetRoot}{Path.GetDirectoryName(clr.ModuleInfo.FileName)}/libmscordaccore.so";
        ClrRuntime runtime = dataTarget.FileLocator is TargetRootLocator && File.Exists(targetDac)
            ? clr.CreateRuntime(targetDac, ignoreMismatch: true)
            : clr.CreateRuntime();
        if (!runtime.Heap.CanWalkHeap)
            throw new InvalidOperationException("The heap cannot be walked right now (the process is probably in the middle of a GC). Try again.");

        return runtime;
    }

    private sealed class TargetRootLocator : IFileLocator
    {
        public string? FindPEImage(string fileName, int buildTimeStamp, int imageSize, bool checkProperties) => Find(fileName);

        public string? FindPEImage(string fileName, SymbolProperties archivedUnder, ImmutableArray<byte> buildIdOrUUID, OSPlatform originalPlatform, bool checkProperties) =>
            Find(fileName);

        private static string? Find(string fileName)
        {
            string path = ContainerSidecar.TargetRoot + fileName;
            return fileName.StartsWith('/') && File.Exists(path) ? path : null;
        }
    }
}
