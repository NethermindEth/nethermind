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
// 

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private class FileConsensusDataSource<T> : IConsensusDataSource<T>, IDisposable
        {
            private readonly Uri _file;
            private readonly IJsonSerializer _serializer;
            
            public FileConsensusDataSource(Uri file, IJsonSerializer serializer)
            {
                _file = file;
                _serializer = serializer;
            }
            
            public async Task<(T, string)> GetData()
            {
                string jsonData = await GetJsonData();
                return (_serializer.Deserialize<T>(jsonData), jsonData);
            }

            public async Task<string> GetJsonData() => await File.ReadAllTextAsync(_file.AbsolutePath);

            public void Dispose() { }
        }
    }
}
