// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
