// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading.Tasks;

namespace Nethermind.Serialization.Json
{
    public interface IJsonSerializer
    {
        T Deserialize<T>(Stream stream);
        T Deserialize<T>(string json);
        string Serialize<T>(T value, bool indented = false);
        long Serialize<T>(Stream stream, T value, bool indented = false);
        ValueTask<long> SerializeAsync<T>(Stream stream, T value, bool indented = false);
    }
}
