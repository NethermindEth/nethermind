// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using Nethermind.Logging;

namespace Nethermind.Core.Extensions
{
    public static class DbDriveInfoProvider
    {
        private static readonly StringComparison PathComparison =
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// Gets the drives that hold the database directory and the directories linked from it.
        /// </summary>
        /// <returns>
        /// The drive of <paramref name="dbPath"/> first, then every other drive that one of its linked
        /// subdirectories resolves to, each drive once.
        /// </returns>
        public static IDriveInfo[] GetDriveInfos(this IFileSystem fileSystem, string dbPath)
        {
            IDirectoryInfo topDir = fileSystem.DirectoryInfo.New(Path.TrimEndingDirectorySeparator(dbPath));
            if (!topDir.Exists)
            {
                if (!CreateDirectorySilent(topDir))
                    return [];
            }
            //the following processing is to overcome specific behaviour on linux where creating DriveInfo for multiple paths on same logical drive
            //gives instances with these paths (and not logical drive)
            // DriveInfo.GetDrives() crashes with a fatal AccessViolationException
            // on macOS ARM64 in native GetAllMountPoints().
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return [];

            IDriveInfo[] allDrives = fileSystem.DriveInfo.GetDrives();
            HashSet<IDriveInfo> seen = [];
            // Ordered, so the top-level drive comes first for callers that take a single drive
            List<IDriveInfo> driveInfos = [];
            AddDriveOf(topDir);

            foreach (IDirectoryInfo di in topDir.EnumerateDirectories())
            {
                //only want to handle symlinks - otherwise will be on same drive as parent
                if (di.LinkTarget is not null)
                {
                    AddDriveOf(di);
                }
            }

            return driveInfos.ToArray();

            void AddDriveOf(IDirectoryInfo dir)
            {
                IDriveInfo? drive = FindDrive(allDrives, ResolvePath(fileSystem, dir));
                if (drive is not null && seen.Add(drive))
                {
                    driveInfos.Add(drive);
                }
            }
        }

        /// <summary>
        /// Finds the drive that holds <paramref name="path"/>: the longest root that is the path itself or one of its
        /// parent directories.
        /// </summary>
        internal static IDriveInfo? FindDrive(IDriveInfo[] drives, string path)
        {
            IDriveInfo? result = null;
            int resultRootLength = -1;
            foreach (IDriveInfo drive in drives)
            {
                string root = drive.RootDirectory.FullName;
                if (root.Length > resultRootLength && IsUnderRoot(path, root))
                {
                    result = drive;
                    resultRootLength = root.Length;
                }
            }

            return result;
        }

        // A root matches on whole path components: /data1 holds /data1 and /data1/db, but not /data10
        private static bool IsUnderRoot(string path, string root) =>
            path.StartsWith(root, PathComparison)
            && (path.Length == root.Length
                || Path.EndsInDirectorySeparator(root)
                || path[root.Length] == Path.DirectorySeparatorChar
                || path[root.Length] == Path.AltDirectorySeparatorChar);

        /// <summary>
        /// The path of a directory with its links followed, or the directory's own path when it is not under a link.
        /// </summary>
        /// <remarks>
        /// On Unix every component is followed from the root, so a database directory under a linked parent directory
        /// resolves to the volume of the link target. On Windows only a linked directory itself is resolved.
        /// A listed link that cannot be resolved (for example, it changed after the directory was listed) falls back to
        /// its own path, the drive that holds the link, so it never stops the node from starting. Dangling and looping
        /// links are not listed as directories on Linux, so they are skipped before this.
        /// </remarks>
        private static string ResolvePath(IFileSystem fileSystem, IDirectoryInfo dir)
        {
            try
            {
                return OperatingSystem.IsWindows()
                    ? dir.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dir.FullName
                    : FollowLinks(fileSystem, dir.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ILogger logger = Static.LogManager.GetClassLogger(typeof(DbDriveInfoProvider));
                if (logger.IsWarn) logger.Warn($"Cannot resolve the linked database directory {dir.FullName:hide}, so the free space of its target is not monitored: {e.Message:hide}");
                return dir.FullName;
            }
        }

        /// <summary>
        /// Follows every link in <paramref name="path"/> one component at a time from the root, as the kernel does.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="IFileSystemInfo.ResolveLinkTarget"/>, which resolves only the last component and removes
        /// ".." from the target text, this applies ".." to the target of the link before it. The root is never inspected.
        /// </remarks>
        /// <exception cref="IOException">More than <see cref="MaxFollowedLinks"/> links are followed.</exception>
        private static string FollowLinks(IFileSystem fileSystem, string path)
        {
            Stack<string> names = new();
            PushNames(names, path);
            string resolved = Path.GetPathRoot(path)!;
            int followed = 0;
            while (names.TryPop(out string? name))
            {
                if (name == ".")
                    continue;

                if (name == "..")
                {
                    resolved = Path.GetDirectoryName(resolved) ?? resolved;
                    continue;
                }

                string next = Path.Join(resolved, name);
                string? target = fileSystem.DirectoryInfo.New(next).LinkTarget;
                if (target is null)
                {
                    resolved = next;
                    continue;
                }

                if (++followed > MaxFollowedLinks)
                    throw new IOException("Too many levels of symbolic links");

                if (Path.IsPathRooted(target))
                    resolved = Path.GetPathRoot(target)!;
                PushNames(names, target);
            }

            return resolved;

            static void PushNames(Stack<string> names, string path)
            {
                string[] parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                for (int i = parts.Length - 1; i >= 0; i--)
                    names.Push(parts[i]);
            }
        }

        // As the Linux kernel (MAXSYMLINKS) and ResolveLinkTarget on Unix
        private const int MaxFollowedLinks = 40;

        private static bool CreateDirectorySilent(IDirectoryInfo directoryInfo)
        {
            try
            {
                directoryInfo.Create();
            }
            catch (IOException e)
            {
                // ignored
                ILogger logger = Static.LogManager.GetClassLogger(typeof(DbDriveInfoProvider));
                if (logger.IsDebug) logger.Debug($"Cannot create the database directory {directoryInfo.FullName:hide}: {e.Message:hide}");
            }
            return directoryInfo.Exists;
        }
    }
}
