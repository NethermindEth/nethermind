//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.Core.Extensions
{
    public static class StringExtensions
    {
        public static bool IsHex(this string value, bool allowHexPrefix = false)
        {
            if (string.IsNullOrEmpty(value)) return false;

            int offset = 0;
            if (value.StartsWith("0x"))
            {
                if (!allowHexPrefix) return false;
                offset += 2;
            }
            if(offset < value.Length)
            {
                for (; offset < value.Length; offset++)
                {
                    char c = value[offset];
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }
    }
}
