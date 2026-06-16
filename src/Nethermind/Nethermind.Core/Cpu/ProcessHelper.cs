// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System;
using System.Diagnostics;

namespace Nethermind.Core.Cpu;

internal static class ProcessHelper
{
    /// <summary>
    /// Run external process and return the console output.
    /// In the case of any exception, null will be returned.
    /// </summary>
    internal static string? RunAndReadOutput(string fileName, string arguments = "")
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = fileName,
            WorkingDirectory = "",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (Process process = new()
        { StartInfo = processStartInfo })
        using (new ConsoleExitHandler(process))
        {
            try
            {
                process.Start();
            }
            catch (Exception)
            {
                return null;
            }
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
    }
}
