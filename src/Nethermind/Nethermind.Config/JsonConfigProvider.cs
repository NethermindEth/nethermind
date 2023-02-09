// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Config
{
    public class JsonConfigProvider : IConfigProvider
    {
        private ConfigProvider _provider = new();

        public JsonConfigProvider(string jsonConfigFile)
        {
            _provider.AddSource(new JsonConfigSource(jsonConfigFile));
        }

        public T GetConfig<T>() where T : IConfig
        {
            return _provider.GetConfig<T>();
        }

        public object GetRawValue(string category, string name)
        {
            return _provider.GetRawValue(category, name);
        }

        public void AddSource(IConfigSource configSource)
        {
            _provider.AddSource(configSource);
        }

        /// <summary>
        /// AFAIK only used in tests and categories and not registered
        /// </summary>
        /// <param name="category"></param>
        /// <param name="configType"></param>
        /// <exception cref="NotSupportedException"></exception>
        public void RegisterCategory(string category, Type configType)
        {
            throw new NotSupportedException();
        }
    }
}
