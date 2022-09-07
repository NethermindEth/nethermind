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

using System.IO.Compression;
using Nethermind.Core.Collections;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.TraceStore;

public static class TraceSerializer
{
    private static readonly IJsonSerializer _jsonSerializer = new EthereumJsonSerializer();
    private static readonly byte[] _emptyBytes = { 0 };
    private static readonly List<ParityLikeTxTrace> _emptyTraces = new();

    public static unsafe List<ParityLikeTxTrace>? Deserialize(Span<byte> serialized)
    {
        if (serialized.Length == 1) return _emptyTraces;

        fixed (byte* pBuffer = &serialized[0])
        {
            using UnmanagedMemoryStream input = new(pBuffer, serialized.Length);
            using GZipStream compressionStream = new(input, CompressionMode.Decompress);
            using ByteArrayPoolList arrayPoolList = new(1024 * 16);
            Span<byte> buffer = stackalloc byte[1024];
            int bytesRead = compressionStream.Read(buffer);
            while (bytesRead > 0)
            {
                arrayPoolList.AddRange(buffer.Slice(0, bytesRead));
                bytesRead = compressionStream.Read(buffer);
            }

            return _jsonSerializer.Deserialize<List<ParityLikeTxTrace>>(arrayPoolList.AsMemoryStream());
        }
    }

    public static byte[] Serialize(IReadOnlyCollection<ParityLikeTxTrace> traces)
    {
        if (traces.Count == 0) return _emptyBytes;

        using MemoryStream output = new();
        using (GZipStream compressionStream = new(output, CompressionMode.Compress))
        {
            _jsonSerializer.Serialize(compressionStream, traces);
        }

        return output.ToArray();
    }
}
