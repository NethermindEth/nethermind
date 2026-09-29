// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;

namespace Nethermind.Torrent.Maui;

internal static class TorrentFileReveal
{
    public static async Task<bool> RevealAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            using Process? process = Process.Start(CreateWindowsStartInfo(path, isFile));
            return process is not null;
        }

        if (OperatingSystem.IsLinux())
        {
            if (isFile && await TryRunAsync(CreateLinuxSelectStartInfo(new Uri(path)), cancellationToken))
            {
                return true;
            }

            string directory = isFile ? Path.GetDirectoryName(path)! : path;
            if (await TryRunAsync(CreateLinuxOpenStartInfo("xdg-open", directory), cancellationToken) ||
                await TryRunAsync(CreateLinuxOpenStartInfo("gio", directory), cancellationToken))
            {
                return true;
            }

            throw new InvalidOperationException("No desktop file manager could open the download directory.");
        }

        throw new PlatformNotSupportedException("File reveal is supported on Windows and Linux desktops.");
    }

    internal static ProcessStartInfo CreateWindowsStartInfo(string path, bool isFile)
        => new("explorer.exe")
        {
            UseShellExecute = false,
            Arguments = isFile ? "/select,\"" + path + "\"" : "\"" + path + "\"",
        };

    internal static ProcessStartInfo CreateLinuxSelectStartInfo(Uri fileUri)
    {
        ProcessStartInfo startInfo = new("dbus-send") { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add("--print-reply");
        startInfo.ArgumentList.Add("--dest=org.freedesktop.FileManager1");
        startInfo.ArgumentList.Add("/org/freedesktop/FileManager1");
        startInfo.ArgumentList.Add("org.freedesktop.FileManager1.ShowItems");
        startInfo.ArgumentList.Add("array:string:" + fileUri.AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal));
        startInfo.ArgumentList.Add("string:");
        return startInfo;
    }

    internal static ProcessStartInfo CreateLinuxOpenStartInfo(string command, string directory)
    {
        ProcessStartInfo startInfo = new(command) { UseShellExecute = false, CreateNoWindow = true };
        if (command == "gio")
        {
            startInfo.ArgumentList.Add("open");
        }

        startInfo.ArgumentList.Add(directory);
        return startInfo;
    }

    private static async Task<bool> TryRunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                return false;
            }
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
