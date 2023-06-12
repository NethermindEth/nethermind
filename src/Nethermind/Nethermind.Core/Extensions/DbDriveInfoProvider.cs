// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;

namespace Nethermind.Core.Extensions
{
    public static class DbDriveInfoProvider
    {
        public static IDriveInfo[] GetDriveInfos(this IFileSystem fileSystem, string dbPath)
        {
            static IDriveInfo FindDriveForDirectory(IDriveInfo[] drives, DirectoryInfo dir)
            {
                string dPath = dir.LinkTarget ?? dir.FullName;
                IEnumerable<IDriveInfo> candidateDrives = drives.Where(drive => dPath.StartsWith(drive.RootDirectory.FullName));
                IDriveInfo? result = null;
                foreach (IDriveInfo driveInfo in candidateDrives)
                {
                    result ??= driveInfo;
                    if (driveInfo.RootDirectory.FullName.Length > result.RootDirectory.FullName.Length)
                    {
                        result = driveInfo;
                    }
                }

                return result!;
            }

            DirectoryInfo topDir = new(dbPath);
            if (topDir.Exists)
            {
                HashSet<IDriveInfo> driveInfos = new();
                //the following processing is to overcome specific behaviour on linux where creating DriveInfo for multiple paths on same logical drive
                //gives instances with these paths (and not logical drive)
                IDriveInfo[] allDrives = fileSystem.DriveInfo.GetDrives();
                IDriveInfo topLevelDrive = FindDriveForDirectory(allDrives, topDir);
                if (topLevelDrive is not null)
                {
                    driveInfos.Add(topLevelDrive);
                }

                foreach (DirectoryInfo di in topDir.EnumerateDirectories())
                {
                    //only want to handle symlinks - otherwise will be on same drive as parent
                    if (di.LinkTarget is not null)
                    {
                        IDriveInfo matchedDrive = FindDriveForDirectory(allDrives, topDir);
                        if (matchedDrive is not null)
                        {
                            driveInfos.Add(matchedDrive);
                        }
                    }
                }

                return driveInfos.ToArray();
            }

            return Array.Empty<IDriveInfo>();
        }
    }
}
