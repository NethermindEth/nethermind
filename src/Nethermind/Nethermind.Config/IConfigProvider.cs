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

namespace Nethermind.Config
{
    public interface IConfigProvider
    {    
        /// <summary>
        /// Gets a parsed configuration type. It contains the data from all the config sources combined.
        /// </summary>
        /// <typeparam name="T">Type of the configuration interface.</typeparam>
        /// <returns>The configuration object.</returns>
        T GetConfig<T>() where T : IConfig;
        
        /// <summary>
        /// Gets the value exactly in the format of the configuration data source.
        /// </summary>
        /// <param name="category">Configuration category (e.g. Init).</param>
        /// <param name="name">Name of the configuration property.</param>
        /// <returns></returns>
        object GetRawValue(string category, string name);
    }
}
