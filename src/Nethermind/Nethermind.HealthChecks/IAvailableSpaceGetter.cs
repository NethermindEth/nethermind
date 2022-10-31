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
        (long, double) GetAvailableSpace(string location);
    }

    public class AvailableSpaceGetter : IAvailableSpaceGetter
    {
        /// <summary>
        /// Returns free space in bytes and as percentage of total space on disk derived from the passed location
        /// </summary>
        /// <param name="location">Storage path to check</param>
        /// <returns></returns>
        public (long, double) GetAvailableSpace(string location)
        {
            DriveInfo di = new(location);
            double freeSpacePcnt = (double)di.AvailableFreeSpace / di.TotalSize * 100;
            return new(di.AvailableFreeSpace, freeSpacePcnt);
        }
    }
}
