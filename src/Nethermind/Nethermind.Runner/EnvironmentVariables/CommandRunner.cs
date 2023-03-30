// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nethermind.Runner.EnvironmentVariables;

public static class CommandRunner
{
    public const int CouldNotStartProcess = int.MaxValue;

    public static int RunCommand(string command, string arguments)
    {
        ProcessStartInfo info = new(command, arguments)
        {
            UseShellExecute = true,
            CreateNoWindow = true,
        };
        Process? process = Process.Start(info);
        if (process is not null)
        {
            process.WaitForExit();
            return process.ExitCode;
        }

        return CouldNotStartProcess;
    }
}
