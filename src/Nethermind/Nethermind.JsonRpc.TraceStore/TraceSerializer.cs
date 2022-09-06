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

using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore;

public static class TraceSerializer
{
    private static readonly JsonSerializerOptions _jsonSerializerOptions = new() { Converters = { new KeccakUtf8Converter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    public static unsafe TCollection? Deserialize<TCollection>(Span<byte> serialized) where TCollection : IEnumerable<ParityLikeTxTrace>
    {
        fixed (byte* pBuffer = &serialized[0])
        {
            using UnmanagedMemoryStream input = new(pBuffer, serialized.Length);
            using GZipStream compressionStream = new(input, CompressionMode.Decompress);
            using ArrayPoolList<byte> arrayPoolList = new(1024 * 16);
            Span<byte> buffer = stackalloc byte[1024];
            int bytesRead = compressionStream.Read(buffer);
            while (bytesRead > 0)
            {
                arrayPoolList.AddRange(buffer.Slice(0, bytesRead));
                bytesRead = compressionStream.Read(buffer);
            }

            return JsonSerializer.Deserialize<TCollection>(arrayPoolList.AsSpan(), _jsonSerializerOptions);
        }
    }

    public static byte[] Serialize(IReadOnlyCollection<ParityLikeTxTrace> traces)
    {
        using MemoryStream output = new();
        using (GZipStream compressionStream = new(output, CompressionMode.Compress))
        {
            JsonSerializer.Serialize(compressionStream, traces, _jsonSerializerOptions);
        }

        return output.ToArray();
    }
}
