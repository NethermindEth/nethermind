// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Logging;

namespace Nethermind.Runner.EnvironmentVariables;

public static class EnvironmentVariable
{
    public static bool TrySetEnvironmentVariable(string variable, string value, ILogger logger)
    {
        (string command, string arguments) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ("setx", $"{variable} {value}")
            : ("export", $"{variable}={value}");

        try
        {
            if (logger.IsInfo) logger.Info($"Setting environment variable {variable} to {value}");

            int exitCode = CommandRunner.RunCommand(command, arguments);

            if (exitCode == 0)
            {
                if (logger.IsInfo) logger.Info($"Successfully set environment variable {variable} to {value}");
            }
            else if (exitCode == CommandRunner.CouldNotStartProcess)
            {
                if (logger.IsError) logger.Error($"Failed to set environment variable {variable} to {value}. Couldn't start external process.");
            }
            else
            {
                if (logger.IsError) logger.Error($"Failed to set environment variable {variable} to {value}. Exit code: {exitCode}.");
            }

            return exitCode == 0;
        }
        catch (Exception e)
        {
            if (logger.IsError)
            {
                logger.Error($"Failed to set environment variable {variable} to {value}.", e);
            }

            return false;
        }
    }
}
