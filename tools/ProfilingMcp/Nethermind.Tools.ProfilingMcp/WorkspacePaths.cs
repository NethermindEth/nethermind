// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.ProfilingMcp;

/// <summary>
/// Directories where the server keeps downloaded profiler tools, snapshots, and reports.
/// </summary>
/// <remarks>
/// Defaults to a short path under the local application data folder: the dotTrace console tools are
/// .NET Framework executables on Windows and fail silently (exit code -1, no output) when started from deeply nested paths.
/// Override with the <c>PROFILING_MCP_HOME</c> environment variable.
/// </remarks>
public sealed class WorkspacePaths
{
    private WorkspacePaths(string root)
    {
        Root = root;
        Tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
        Snapshots = Directory.CreateDirectory(Path.Combine(root, "snapshots")).FullName;
        Heap = Directory.CreateDirectory(Path.Combine(root, "heap")).FullName;
    }

    public string Root { get; }

    public string Tools { get; }

    /// <summary>dotTrace snapshots and their Reporter output.</summary>
    public string Snapshots { get; }

    /// <summary>Heap statistics JSON files and process dumps.</summary>
    public string Heap { get; }

    public static WorkspacePaths FromEnvironment()
    {
        string? root = Environment.GetEnvironmentVariable("PROFILING_MCP_HOME");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProfilingMcp");

        return new WorkspacePaths(root);
    }

    internal static WorkspacePaths At(string root) => new(root);

    /// <summary>Returns a timestamped file name, e.g. <c>20261029-113045-demo.dtp</c>.</summary>
    public static string NewFileName(string label, string extension)
    {
        Span<char> sanitized = stackalloc char[Math.Min(label.Length, 40)];
        for (int i = 0; i < sanitized.Length; i++)
            sanitized[i] = char.IsLetterOrDigit(label[i]) ? label[i] : '-';

        return $"{DateTime.Now:yyyyMMdd-HHmmss}-{sanitized}{extension}";
    }
}
