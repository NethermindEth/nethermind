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
using System.Linq;

namespace Nethermind.HealthChecks
{
    public interface IAvailableSpaceGetter
    {
        IEnumerable<(long, double)> GetAvailableSpace();
    }

    public class AvailableSpaceGetter : IAvailableSpaceGetter
    {
        private readonly DriveInfo[] _driveInfos;
        public AvailableSpaceGetter(string location)
        {
            DirectoryInfo diTop = new(location);
            if (diTop.Exists)
            {
                HashSet<DriveInfo> driveInfos = new();
                //the following processing is to overcome specific behaviour on linux where creating DriveInfo for multiple paths on same logical drive
                //gives instances with these paths (and not logical drive)
                DriveInfo[] allDrives = DriveInfo.GetDrives();
                DriveInfo topLevelDrive = FindDriveForDirectory(allDrives, diTop);
                if (topLevelDrive != null)
                    driveInfos.Add(topLevelDrive);

                foreach (DirectoryInfo di in diTop.EnumerateDirectories())
                {
                    //only want to handle symlinks - otherwise will be on same drive as parent
                    if (di.LinkTarget != null)
                    {
                        DriveInfo matchedDrive = FindDriveForDirectory(allDrives, diTop);
                        if (matchedDrive != null)
                            driveInfos.Add(matchedDrive);
                    }
                }
                _driveInfos = driveInfos.ToArray();
            }
            else
            {
                _driveInfos = Array.Empty<DriveInfo>();
            }
        }

        private static DriveInfo FindDriveForDirectory(DriveInfo[] drives, DirectoryInfo di)
        {
            string dPath = di.LinkTarget ?? di.FullName;
            IEnumerable<DriveInfo> candidateDrives = drives.Where(drive => dPath.StartsWith(drive.RootDirectory.FullName));
            if (candidateDrives.Any())
            {
                //get the longest matching drive path (avoid '/' on linux)
                return candidateDrives.Aggregate(candidateDrives.First(),
                                    (max, cur) => max.RootDirectory.FullName.Length > cur.RootDirectory.FullName.Length ? max : cur);
            }
            return null;
        }

        /// <summary>
        /// Returns free space in bytes and as percentage of total space on all logical drives derived from the location used to construct
        /// </summary>
        /// <returns></returns>
        public IEnumerable<(long, double)> GetAvailableSpace()
        {
            foreach (DriveInfo driveInfo in _driveInfos)
            {
                double freeSpacePcnt = (double)driveInfo.AvailableFreeSpace / driveInfo.TotalSize * 100;
                yield return new(driveInfo.AvailableFreeSpace, freeSpacePcnt);
            }
        }
    }
}
