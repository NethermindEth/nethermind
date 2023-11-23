// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Compression;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore;

public class ParityLikeTraceSerializer : ITraceSerializer<ParityLikeTxTrace>
{
    private static readonly byte[] _emptyBytes = { 0 };
    private static readonly List<ParityLikeTxTrace> _emptyTraces = new();

    private readonly ILogger? _logger;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly int _maxDepth;
    private readonly bool _verifySerialized;

    public ParityLikeTraceSerializer(ILogManager logManager, int maxDepth = 1024, bool verifySerialized = false)
    {
        _jsonSerializer = new EthereumJsonSerializer(maxDepth, new ParityTraceActionCreationConverter());
        _maxDepth = maxDepth;
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

        CheckDepth(traces);

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

    private void CheckDepth(IReadOnlyCollection<ParityLikeTxTrace> parityLikeTxTraces)
    {
        int depth = 0;
        foreach (ParityLikeTxTrace trace in parityLikeTxTraces)
        {
            if (trace.Action is not null)
            {
                CheckDepth(trace.Action, depth);
            }
        }
    }

    private void CheckDepth(ParityTraceAction action, int depth)
    {
        depth++;

        if (depth >= _maxDepth)
        {
            throw new ArgumentException("Trace depth is too high");
        }

        foreach (ParityTraceAction subAction in action.Subtraces)
        {
            CheckDepth(subAction, depth);
        }
    }
}
