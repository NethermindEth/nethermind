// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.ProfilingMcp.DotTrace;

/// <summary>
/// A <c>##dotTrace["name", {...}]</c> service message exchanged with the dotTrace console profiler over stdin/stdout.
/// </summary>
/// <remarks>
/// See <c>dottrace help service-messages</c>. Output lines may start with a byte order mark.
/// </remarks>
internal readonly record struct ServiceMessage(string Name, int? Pid, string? Path)
{
    private const string Prefix = "##dotTrace";

    public static bool TryParse(string line, out ServiceMessage message)
    {
        message = default;
        ReadOnlySpan<char> span = line.AsSpan().Trim().TrimStart('﻿');
        if (!span.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(span[Prefix.Length..].ToString());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.String)
                return false;

            int? pid = null;
            string? path = null;
            if (root.GetArrayLength() > 1 && root[1].ValueKind == JsonValueKind.Object)
            {
                JsonElement args = root[1];
                if (args.TryGetProperty("pid", out JsonElement pidElement) && pidElement.TryGetInt32(out int parsedPid))
                    pid = parsedPid;
                if (args.TryGetProperty("filename", out JsonElement filename))
                    path = filename.GetString();
                else if (args.TryGetProperty("path", out JsonElement executable))
                    path = executable.GetString();
            }

            message = new ServiceMessage(root[0].GetString()!, pid, path);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Formats a command for the profiler's stdin.</summary>
    /// <remarks>The profiler requires every message to start on a new line and end with a carriage return.</remarks>
    public static string FormatCommand(string name, int? pid) =>
        pid is null ? $"\r\n{Prefix}[\"{name}\"]\r\n" : $"\r\n{Prefix}[\"{name}\",{{\"pid\":{pid}}}]\r\n";
}
