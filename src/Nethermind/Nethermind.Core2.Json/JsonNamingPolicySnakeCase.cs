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
using System.Text;
using System.Text.Json;

namespace Nethermind.Core2.Json
{
    public class JsonNamingPolicySnakeCase : JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }

            int convertedLength = name.Length;
            if (name.Length > 1)
            {
                for (var index = 1; index < name.Length; index++)
                {
                    if (char.IsUpper(name[index]))
                    {
                        convertedLength++;
                    }
                }
            }

            string context = name;
            
            string converted = string.Create(convertedLength, context, (chars, state) =>
            {
                int position = 0;
                for (var sourceIndex = 0; sourceIndex < state.Length; sourceIndex++)
                {
                    char c = state[sourceIndex];
                    if (sourceIndex == 0)
                    {
                        chars[position++] = char.ToLowerInvariant(c);
                    }
                    else
                    {
                        if (char.IsUpper(c))
                        {
                            chars[position++] = '_';
                            chars[position++] = char.ToLowerInvariant(c);
                        }
                        else
                        {
                            chars[position++] = c;
                        }
                    }
                }
            });

            return converted;
        }
    }
}