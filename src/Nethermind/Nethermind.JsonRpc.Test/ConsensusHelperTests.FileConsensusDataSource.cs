// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;

using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private class FileConsensusDataSource<T>(Uri file, IJsonSerializer serializer) : IConsensusDataSource<T>, IDisposable
        {
            private readonly Uri _file = file;
            private readonly IJsonSerializer _serializer = serializer;

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
