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
