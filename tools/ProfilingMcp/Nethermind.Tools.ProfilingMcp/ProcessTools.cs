// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using ModelContextProtocol.Server;

namespace Nethermind.Tools.ProfilingMcp;

[McpServerToolType]
public sealed class ProcessTools
{
    [McpServerTool(Name = "list_dotnet_processes", Title = "List .NET processes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists running .NET (Core 3.0+) processes that expose a diagnostics endpoint, with PID, name, working set and executable path.")]
    public string ListProcesses([Description("Only processes whose name or path contains this text (case-insensitive).")] string? filter = null)
    {
        TextTable table = new("pid", "working set", "name", "path");
        foreach (int pid in DiagnosticsClient.GetPublishedProcesses().Order())
        {
            if (pid == Environment.ProcessId)
                continue;

            try
            {
                using Process process = Process.GetProcessById(pid);
                string path = TryGetPath(process);
                if (filter is not null &&
                    !process.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                table.Add(pid.ToString(CultureInfo.InvariantCulture), $"{process.WorkingSet64 >> 20:N0} MB", process.ProcessName, path);
            }
            catch (ArgumentException)
            {
                // Exited between enumeration and inspection.
            }
        }

        return table.Count == 0 ? "No matching .NET processes." : table.ToString();
    }

    private static string TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? "";
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // Another user's process, or it exited.
            return "";
        }
    }
}
