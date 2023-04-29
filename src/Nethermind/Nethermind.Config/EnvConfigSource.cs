// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Collections.Generic;

namespace Nethermind.Config
{
    public class EnvConfigSource : IConfigSource
    {
        private IEnvironment _environmentWrapper;

        public EnvConfigSource() : this(new EnvironmentWrapper())
        {
        }

        public EnvConfigSource(IEnvironment environmentWrapper)
        {
            _environmentWrapper = environmentWrapper;
        }

        public (bool IsSet, object Value) GetValue(Type type, string category, string name)
        {
            (bool isSet, string value) = GetRawValue(category, name);
            return (isSet, isSet ? ConfigSourceHelper.ParseValue(type, value, category, name) : ConfigSourceHelper.GetDefault(type));
        }

        public (bool IsSet, string Value) GetRawValue(string category, string name)
        {
            var variableName = string.IsNullOrEmpty(category) ? $"NETHERMIND_{name.ToUpperInvariant()}" : $"NETHERMIND_{category.ToUpperInvariant()}_{name.ToUpperInvariant()}";
            var variableValueString = _environmentWrapper.GetEnvironmentVariable(variableName);
            return string.IsNullOrWhiteSpace(variableValueString) ? (false, null) : (true, variableValueString);
        }

        public IEnumerable<(string Category, string Name)> GetConfigKeys()
        {
            return _environmentWrapper.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("NETHERMIND_")).Select(v => v.Split('_')).Select(a =>
            {
                // actually only possible value is "NETHERMIND_"
                if (a.Length <= 1)
                {
                    return (null, null);
                }

                // variables like "NETHERMIND_URL", "NETHERMIND_CONFIG" ...
                if (a.Length == 2)
                {
                    return (null, a[1]);
                }

                // VARIABLES like "NETHERMIND_CLI_SWITCH_LOCAL", "NETHERMIND_MONITORING_JOB" ...
                if (a.Length > 2 && !a[1].EndsWith("config", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, string.Join(null, a[1..]));
                }

                // Variables like "NETHERMIND_JSONRPCCONFIG_ENABLED" ...
                return (a[1], a[2]);
            });
        }
    }

    public interface IEnvironment
    {
        string GetEnvironmentVariable(string variableName);
        System.Collections.IDictionary GetEnvironmentVariables();
    }

    public class EnvironmentWrapper : IEnvironment
    {
        public string GetEnvironmentVariable(string variableName)
        {
            return Environment.GetEnvironmentVariable(variableName);
        }

        public System.Collections.IDictionary GetEnvironmentVariables()
        {
            return Environment.GetEnvironmentVariables();
        }
    }
}
