// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
