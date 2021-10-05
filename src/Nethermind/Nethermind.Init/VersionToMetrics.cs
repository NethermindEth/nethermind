//  Copyright (c) 2021 Demerzel Solutions Limited
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

namespace Nethermind.Init
{
    public static class VersionToMetrics
    {
        public static int ConvertToNumber(string version)
        {
            try
            {
                int indexOfDash = version.IndexOf('-');
                indexOfDash = indexOfDash == -1 ? version.Length : indexOfDash;
                int prefixLength = version.StartsWith('v') ? 1 : 0;
                string numberString = version.AsSpan(prefixLength, indexOfDash - prefixLength).ToString();
                string[] versionParts = numberString.Split(".");
                int result = 100_000 * int.Parse(versionParts[0]);
                result += 1000 * int.Parse(versionParts[1]);
                result += 1 * int.Parse(versionParts[2]);
                return result;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
