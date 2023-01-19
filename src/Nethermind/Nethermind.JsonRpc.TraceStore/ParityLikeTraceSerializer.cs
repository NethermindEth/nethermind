// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using Nethermind.Core.Collections;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore;

public class ParityLikeTraceSerializer : ITraceSerializer<ParityLikeTxTrace>
{
    private static readonly byte[] _emptyBytes = { 0 };
    private static readonly List<ParityLikeTxTrace> _emptyTraces = new();

    private readonly ILogger? _logger;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly bool _verifySerialized;

    public ParityLikeTraceSerializer(ILogManager logManager, int maxDepth = 1024, bool verifySerialized = false)
    {
        _jsonSerializer = new EthereumJsonSerializer(maxDepth, new ParityTraceActionCreationConverter());
        _verifySerialized = verifySerialized;
        _logger = logManager?.GetClassLogger<ParityLikeTraceSerializer>();
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
