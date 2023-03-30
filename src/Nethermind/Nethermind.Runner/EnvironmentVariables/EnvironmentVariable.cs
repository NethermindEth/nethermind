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
        string command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"setx {variable} {value}"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? $"echo 'export {variable}={value}' >> ~/.zshrc"
                : $"echo 'export {variable}={value}' >> ~/.bashrc";

        try
        {
            if (logger.IsInfo) logger.Info($"Setting environment variable {variable} to {value}");

            (int exitCode, string output, string error) = CommandRunner.RunCommand(command);

            if (exitCode == 0)
            {
                if (logger.IsInfo) logger.Info($"Successfully set environment variable {variable} to {value}");
            }
            else
            {
                if (logger.IsError) logger.Error($"Failed to set environment variable {variable} to {value}. Exit code: {exitCode}. Output: {output}. Error: {error}");
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
