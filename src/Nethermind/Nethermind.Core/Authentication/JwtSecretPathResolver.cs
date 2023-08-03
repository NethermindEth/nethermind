// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Authentication;

public class JwtSecretPathResolver
{
    private readonly IRuntimePlatformChecker _runtimePlatformChecker;

    public JwtSecretPathResolver(IRuntimePlatformChecker runtimePlatformChecker)
    {
        _runtimePlatformChecker = runtimePlatformChecker;
    }

    public string GetDefaultFilePath()
    {
        if (_runtimePlatformChecker.IsLinux())
        {
            string? xdgDataDir = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

            if (!string.IsNullOrEmpty(xdgDataDir))
                return Path.Combine(xdgDataDir, "ethereum", "engine", "jwt.hex");

            string? homeDir = Environment.GetEnvironmentVariable("HOME");

            if (string.IsNullOrEmpty(homeDir))
                throw new Exception("HOME environment variable is not set");

            return Path.Combine(homeDir, ".local", "share", "ethereum", "engine", "jwt.hex");
        }

        if (_runtimePlatformChecker.IsWindows())
        {
            string appDataDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            return Path.Combine(appDataDir, "Ethereum", "Engine", "jwt.hex");
        }

        if (_runtimePlatformChecker.IsOSX())
        {
            string? homeDir = Environment.GetEnvironmentVariable("HOME");

            if (string.IsNullOrEmpty(homeDir))
                throw new Exception("HOME environment variable is not set");

            return Path.Combine(homeDir, "Library", "Application Support", "Ethereum", "Engine", "jwt.hex");
        }

        throw new NotSupportedException("Unsupported OS");
    }
}

public interface IRuntimePlatformChecker
{
    bool IsWindows();
    bool IsLinux();
    bool IsOSX();
}

public class RuntimePlatformChecker : IRuntimePlatformChecker
{
    public bool IsWindows()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    public bool IsLinux()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }

    public bool IsOSX()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }
}
