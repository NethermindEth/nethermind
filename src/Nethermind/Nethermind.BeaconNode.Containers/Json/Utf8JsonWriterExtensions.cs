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

using System;
using System.Text.Json;

namespace Nethermind.BeaconNode.Containers.Json
{
    public static class Utf8JsonWriterExtensions
    {
        public static void WritePrefixedHexStringValue(this Utf8JsonWriter writer, ReadOnlySpan<byte> bytes)
        {
            var hex = new char[bytes.Length * 2 + 2];
            hex[0] = '0';
            hex[1] = 'x';
            var hexIndex = 2;
            for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
            {
                var s = bytes[byteIndex].ToString("x2");
                hex[hexIndex] = s[0];
                hex[hexIndex + 1] = s[1];
                hexIndex += 2;
            }
            writer.WriteStringValue(hex);
        }
    }
}
