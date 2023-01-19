//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;

namespace Nethermind.HealthChecks
{
    public static class DbDriveInfoProvider
    {
        public static IDriveInfo[] GetDriveInfos(this IFileSystem fileSystem, string dbPath)
        {
            static IDriveInfo FindDriveForDirectory(IDriveInfo[] drives, DirectoryInfo dir)
            {
                string dPath = dir.LinkTarget ?? dir.FullName;
                IEnumerable<IDriveInfo> candidateDrives = drives.Where(drive => dPath.StartsWith(drive.RootDirectory.FullName));
                IDriveInfo result = null;
                foreach (IDriveInfo driveInfo in candidateDrives)
                {
                    result ??= driveInfo;
                    if (driveInfo.RootDirectory.FullName.Length > result.RootDirectory.FullName.Length)
                    {
                        result = driveInfo;
                    }
                }

                return result;
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
