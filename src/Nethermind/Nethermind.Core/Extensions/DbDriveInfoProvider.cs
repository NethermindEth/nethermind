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
                IDriveInfo? drive = FindDrive(allDrives, ResolvePath(dir));
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
        /// The final target of a linked directory, or the directory's own path when it is not a link.
        /// </summary>
        /// <remarks>
        /// A listed link that cannot be resolved (for example, it changed after the directory was listed) falls back to
        /// its own path, the drive that holds the link, so it never stops the node from starting. Dangling and looping
        /// links are not listed as directories on Linux, so they are skipped before this.
        /// </remarks>
        private static string ResolvePath(IDirectoryInfo dir)
        {
            try
            {
                return dir.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dir.FullName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ILogger logger = Static.LogManager.GetClassLogger(typeof(DbDriveInfoProvider));
                if (logger.IsWarn) logger.Warn($"Cannot resolve the linked database directory {dir.FullName:hide}, so the free space of its target is not monitored: {e.Message:hide}");
                return dir.FullName;
            }
        }

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
