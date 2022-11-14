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
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore;

public class TraceSerializer : ITraceSerializer
{
    private static readonly byte[] _emptyBytes = { 0 };
    private static readonly List<ParityLikeTxTrace> _emptyTraces = new();

    private readonly ILogger? _logger;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly bool _verifySerialized;

    public TraceSerializer(ILogManager logManager, int maxDepth = 1024, bool verifySerialized = false)
    {
        _jsonSerializer = new EthereumJsonSerializer(maxDepth);
        _verifySerialized = verifySerialized;
        _logger = logManager?.GetClassLogger<TraceSerializer>();
    }

    public unsafe List<ParityLikeTxTrace>? Deserialize(Span<byte> serialized)
    {
        if (serialized.Length == 1) return _emptyTraces;

        fixed (byte* pBuffer = &serialized[0])
        {
            using UnmanagedMemoryStream input = new(pBuffer, serialized.Length);
            return Deserialize(input);
        }
    }

    public List<ParityLikeTxTrace>? Deserialize(Stream serialized)
    {
        using GZipStream compressionStream = new(serialized, CompressionMode.Decompress);
        return _jsonSerializer.Deserialize<List<ParityLikeTxTrace>>(compressionStream);
    }

    public byte[] Serialize(IReadOnlyCollection<ParityLikeTxTrace> traces)
    {
        if (traces.Count == 0) return _emptyBytes;

        using MemoryStream output = new();
        using (GZipStream compressionStream = new(output, CompressionMode.Compress))
        {
            _jsonSerializer.Serialize(compressionStream, traces);
        }

        byte[] result = output.ToArray();

        // This is for testing
        if (_verifySerialized)
        {
            Task.Run(() =>
            {
                try
                {
                    Deserialize(result);
                }
                catch (Exception e)
                {
                    ParityLikeTxTrace? trace = traces.FirstOrDefault();
                    string tracesWrittenToPath = Path.Combine(Path.GetTempPath(), $"{trace?.BlockNumber}-{trace?.BlockHash}.zip");
                    if (_logger?.IsError == true) _logger.Error($"Can't deserialize trace logs for block {trace?.BlockNumber} ({trace?.BlockHash}), size {result.Length}, dump: {tracesWrittenToPath}", e);
                    File.WriteAllBytes(tracesWrittenToPath, result);
                }
            });
        }

        return result;
    }
}
