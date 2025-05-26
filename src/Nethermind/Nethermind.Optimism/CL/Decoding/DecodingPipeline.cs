// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Optimism.CL.Decoding;

public class DecodingPipeline(ILogManager logManager) : IDecodingPipeline
{
    private readonly Channel<DaDataSource> _inputChannel = Channel.CreateUnbounded<DaDataSource>();
    private readonly Channel<(BatchV1, ulong)> _outputChannel = Channel.CreateUnbounded<(BatchV1, ulong)>();
    private readonly IFrameQueue _frameQueue = new FrameQueue(logManager);
    private readonly ILogger _logger = logManager.GetClassLogger();

    public ChannelWriter<DaDataSource> DaDataWriter => _inputChannel.Writer;
    public ChannelReader<(BatchV1, ulong)> DecodedBatchesReader => _outputChannel.Reader;

    public async Task Run(CancellationToken token)
    {
        var buffer = new Memory<byte>(new byte[BlobDecoder.MaxBlobDataSize]);
        try
        {
            while (!token.IsCancellationRequested)
            {
                Task newDataReady = _inputChannel.Reader.WaitToReadAsync(token).AsTask();
                await Task.WhenAny(newDataReady, _resetRequested.Task);

                if (_resetRequested.Task.IsCompleted)
                {
                    Clear();
                    _resetRequested = new();
                    _resetCompleted.SetResult();
                    continue;
                }

                var daData = await _inputChannel.Reader.ReadAsync(token);
                buffer.Clear();
                try
                {
                    Memory<byte> decodedData;
                    if (daData.DataType == DaDataType.Blob)
                    {
                        var read = BlobDecoder.DecodeBlob(daData.Data, buffer.Span);
                        decodedData = buffer[..read];
                    }
                    else
                    {
                        decodedData = daData.Data;
                    }

                    foreach (var frame in FrameDecoder.DecodeFrames(decodedData))
                    {
                        var batches = _frameQueue.ConsumeFrame(frame);
                        if (batches is not null)
                        {
                            foreach (var batch in batches)
                            {
                                await _outputChannel.Writer.WriteAsync((batch, daData.DataOrigin), token);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    if (_logger.IsWarn) _logger.Warn($"Unhandled exception in decoding pipeline: {e}");
                }
            }
        }
        finally
        {
            if (_logger.IsInfo) _logger.Info($"Decoding pipeline is shutting down.");
        }
    }

    private void Clear()
    {
        while (_inputChannel.Reader.Count > 0)
        {
            while (_inputChannel.Reader.TryRead(out _)) { }
        }

        while (_outputChannel.Reader.Count > 0)
        {
            while (_outputChannel.Reader.TryRead(out _)) { }
        }

        _frameQueue.Clear();
    }

    private TaskCompletionSource _resetCompleted = new();
    private TaskCompletionSource _resetRequested = new();

    public async Task Reset(CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info("Resetting decoding pipeline");

        _resetRequested.SetResult();
        await _resetCompleted.Task;
        _resetCompleted = new();
    }
}
