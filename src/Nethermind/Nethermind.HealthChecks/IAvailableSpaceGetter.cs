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

using System.IO;

namespace Nethermind.HealthChecks
{
    public interface IAvailableSpaceGetter
    {
        (long, double) GetAvailableSpace();
    }

    public class AvailableSpaceGetter : IAvailableSpaceGetter
    {
        private readonly DriveInfo _driveInfo;
        public AvailableSpaceGetter(string location)
        {
            _driveInfo = new(location);
        }

        /// <summary>
        /// Returns free space in bytes and as percentage of total space on disk derived from the location used to construct
        /// </summary>
        /// <returns></returns>
        public (long, double) GetAvailableSpace()
        {
            double freeSpacePcnt = (double)_driveInfo.AvailableFreeSpace / _driveInfo.TotalSize * 100;
            return new(_driveInfo.AvailableFreeSpace, freeSpacePcnt);
        }
    }
}
