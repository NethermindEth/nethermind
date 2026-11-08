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
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // Exited between enumeration and inspection.
            }
        }

        return table.Count == 0 ? "No matching .NET processes." : table.ToString();
    }

    [McpServerTool(Name = "list_containers", Title = "List Docker containers", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lists the running containers of a Docker engine, and the Docker contexts this machine can reach other engines with. " +
                 "To profile a container's processes, pass its name as container, and the engine as dockerHost, to the other tools.")]
    public async Task<string> ListContainersAsync(
        [Description("Docker engine: a context name, or an address such as ssh://user@host or tcp://host:2376. Omit for the engine the docker CLI uses by default.")] string? dockerHost = null,
        [Description("Only containers whose name or image contains this text (case-insensitive).")] string? filter = null)
    {
        TextTable containers = new("container", "image", "status");
        string listing = await ContainerSidecar.DockerAsync(ContainerSidecar.EngineArguments(dockerHost), "ps", "--format", "{{.Names}}\t{{.Image}}\t{{.Status}}");
        foreach (string line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] columns = line.Split('\t');
            if (columns.Length == 3 && !columns[0].StartsWith("profmcp-", StringComparison.Ordinal) &&
                (filter is null || columns[0].Contains(filter, StringComparison.OrdinalIgnoreCase) || columns[1].Contains(filter, StringComparison.OrdinalIgnoreCase)))
                containers.Add(columns[0], columns[1], columns[2]);
        }

        TextTable contexts = new("context", "engine");
        string contextListing = await ContainerSidecar.DockerAsync([], "context", "ls", "--format", "{{.Name}}\t{{.DockerEndpoint}}");
        foreach (string line in contextListing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] columns = line.Split('\t');
            if (columns.Length == 2)
                contexts.Add(columns[0], columns[1]);
        }

        return $"{(containers.Count == 0 ? "No matching running containers." : containers.ToString())}\n\nDocker contexts:\n{contexts}";
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
