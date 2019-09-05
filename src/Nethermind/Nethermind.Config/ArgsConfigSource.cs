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
            return (isSet, isSet ? ConfigSourceHelper.ParseValue(type, value) : ConfigSourceHelper.GetDefault(type));
        }

        public (bool IsSet, string Value) GetRawValue(string category, string name)
        {
            var variableName = $"{category}.{name}";
            bool isSet = _args.ContainsKey(variableName);
            return (isSet, isSet ? _args[variableName] : null);
        }
    }
}