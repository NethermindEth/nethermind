// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Serialization.Json
{
    public interface IJsonSerializer
    {
        object Deserialize(string json, Type type);
        T Deserialize<T>(Stream stream);
        T Deserialize<T>(string json);
        string Serialize<T>(T value, bool indented = false);
        long Serialize<T>(Stream stream, T value, bool indented = false, bool leaveOpen = true);
        ValueTask<long> SerializeAsync<T>(Stream stream, T value, CancellationToken cancellationToken, bool indented = false, bool leaveOpen = true);
        Task SerializeAsync<T>(PipeWriter writer, T value, bool indented = false);
    }
}
