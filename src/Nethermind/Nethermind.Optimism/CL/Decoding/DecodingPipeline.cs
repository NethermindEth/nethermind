// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Optimism.CL.Decoding;

public class DecodingPipeline(ILogger logger) : IDecodingPipeline
{
    private readonly Channel<DaDataSource> _inputChannel = Channel.CreateBounded<DaDataSource>(9);
    private readonly Channel<(BatchV1, ulong)> _outputChannel = Channel.CreateBounded<(BatchV1, ulong)>(3);
    private readonly IFrameQueue _frameQueue = new FrameQueue(logger);

    public ChannelWriter<DaDataSource> DaDataWriter => _inputChannel.Writer;
    public ChannelReader<(BatchV1, ulong)> DecodedBatchesReader => _outputChannel.Reader;

    private int _resetRequested = 0;

    public async Task Run(CancellationToken token)
    {
        var buffer = new Memory<byte>(new byte[BlobDecoder.MaxBlobDataSize]);
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (Interlocked.CompareExchange(ref _resetRequested, 0, 0) == 1)
                {
                    await Clear(token);
                    Interlocked.Exchange(ref _resetRequested, 0);
                    _resetCompleted.SetResult();
                }

                buffer.Clear();
                var daData = await _inputChannel.Reader.ReadAsync(token);

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
                    if (logger.IsWarn) logger.Warn($"Unhandled exception in decoding pipeline: {e}");
                }
            }
        }
        finally
        {
            if (logger.IsInfo) logger.Info($"Decoding pipeline is shutting down.");
        }
    }

    private async Task Clear(CancellationToken token)
    {
        while (await _inputChannel.Reader.WaitToReadAsync(token))
        {
            while (_inputChannel.Reader.TryRead(out _)) { }
        }

        while (await _outputChannel.Reader.WaitToReadAsync(token))
        {
            while (_outputChannel.Reader.TryRead(out _)) { }
        }

        _frameQueue.Clear();
    }

    private TaskCompletionSource _resetCompleted = new();

    public async Task Reset(CancellationToken token)
    {
        Interlocked.Exchange(ref _resetRequested, 1);
        await _resetCompleted.Task;
        _resetCompleted = new();
    }
}
