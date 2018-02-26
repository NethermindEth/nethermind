/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;

namespace Nevermind.Core
{
    public static class Timestamp
    {
        private static readonly DateTime Jan1St1970 = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long UnixUtcUntilNowSecs
        {
            get
            {
                var timestamp = (long)DateTime.UtcNow.Subtract(Jan1St1970).TotalSeconds;
                return timestamp;
            }
        }

        public static long UnixUtcUntilNowMilisecs
        {
            get
            {
                var timestamp = (long)DateTime.UtcNow.Subtract(Jan1St1970).TotalMilliseconds;
                return timestamp;
            }
        }
    }
}