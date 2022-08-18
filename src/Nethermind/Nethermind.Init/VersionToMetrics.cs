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
using System.Linq;

namespace Nethermind.Init;

public static class VersionToMetrics
{
    public static int ConvertToNumber(string version)
    {
        try
        {
            var index = version.IndexOfAny(new[] { '-', '+' });

            if (index != -1)
                version = version[..index];

            var versions = version
                .Split('.')
                .Select(v => int.Parse(v))
                .ToArray();

            return versions.Length == 3
                ? versions[0] * 100_000 + versions[1] * 1_000 + versions[2]
                : throw new ArgumentException("Invalid version format");
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
