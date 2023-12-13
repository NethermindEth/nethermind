// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Nethermind.Init.Cpu;

// we need it public to reuse it in the auto-generated dll
// but we hide it from intellisense with following attribute
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ProcessExtensions
{
    private static readonly TimeSpan DefaultKillTimeout = TimeSpan.FromSeconds(30);

    public static void KillTree(this Process process) => process.KillTree(DefaultKillTimeout);

    public static void KillTree(this Process process, TimeSpan timeout)
    {
        if (RuntimeInformation.IsWindows())
        {
            RunProcessAndIgnoreOutput("taskkill", $"/T /F /PID {process.Id}", timeout);
        }
        else
        {
            var children = new HashSet<int>();
            GetAllChildIdsUnix(process.Id, children, timeout);
            foreach (var childId in children)
            {
                KillProcessUnix(childId, timeout);
            }
            KillProcessUnix(process.Id, timeout);
        }
    }

    private static void KillProcessUnix(int processId, TimeSpan timeout)
        => RunProcessAndIgnoreOutput("kill", $"-TERM {processId}", timeout);

    private static void GetAllChildIdsUnix(int parentId, HashSet<int> children, TimeSpan timeout)
    {
        var (exitCode, stdout) = RunProcessAndReadOutput("pgrep", $"-P {parentId}", timeout);

        if (exitCode == 0 && !string.IsNullOrEmpty(stdout))
        {
            using (var reader = new StringReader(stdout))
            {
                while (true)
                {
                    var text = reader.ReadLine();
                    if (text == null)
                        return;

                    if (int.TryParse(text, out int id) && !children.Contains(id))
                    {
                        children.Add(id);
                        // Recursively get the children
                        GetAllChildIdsUnix(id, children, timeout);
                    }
                }
            }
        }
    }

    private static (int exitCode, string output) RunProcessAndReadOutput(string fileName, string arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        using (Process? process = Process.Start(startInfo))
        {
            if (process is null) return (0, "");

            if (process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                return (process.ExitCode, process.StandardOutput.ReadToEnd());
            }
            else
            {
                process.Kill();
            }

            return (process.ExitCode, "");
        }
    }

    private static int RunProcessAndIgnoreOutput(string fileName, string arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var process = Process.Start(startInfo))
        {
            if (process is null) return 0;

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                process.Kill();

            return process.ExitCode;
        }
    }
}
