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
using System.Collections.Generic;

namespace Nethermind.Config
{
    public class ArgsConfigSource : IConfigSource
    {
        private readonly Dictionary<string, string> _args;

        public ArgsConfigSource(Dictionary<string, string> args)
        {
            _args = new Dictionary<string, string>(args, StringComparer.OrdinalIgnoreCase);
        }

        public (bool IsSet, object Value) GetValue(Type type, string category, string name)
        {
            (bool isSet, string value) = GetRawValue(category?.Replace("Config", string.Empty), name);
            return (isSet, isSet ? ConfigSourceHelper.ParseValue(type, value, category, name) : ConfigSourceHelper.GetDefault(type));
        }

        public (bool IsSet, string Value) GetRawValue(string category, string name)
        {
            var variableName = string.IsNullOrEmpty(category) ? name : $"{category}.{name}";
            bool isSet = _args.ContainsKey(variableName);
            return (isSet, isSet ? _args[variableName] : null);
        }

        public IEnumerable<(string Category, string Name)> GetConfigKeys()
        {
            var argsPairs = _args.Keys.Select(k => k.Split('.')).Select(a =>
            {
                if (a.Length == 0)
                {
                    return (null, null);
                }
                if (a.Length == 1)
                {
                    return (null, a[0]);
                }

                return (a[0], a[1]);
            });

            return argsPairs;
        }
    }
}
