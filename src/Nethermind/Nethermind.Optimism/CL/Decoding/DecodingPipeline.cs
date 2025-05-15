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
    private readonly Channel<DaDataSource> _inputChannel = Channel.CreateBounded<DaDataSource>(9);
    private readonly Channel<BatchV1> _outputChannel = Channel.CreateBounded<BatchV1>(3);
    private readonly FrameQueue _frameQueue = new(logManager);
    private readonly ILogger _logger = logManager.GetClassLogger();

    public ChannelWriter<DaDataSource> DaDataWriter => _inputChannel.Writer;
    public ChannelReader<BatchV1> DecodedBatchesReader => _outputChannel.Reader;

    public async Task Run(CancellationToken token)
    {
        var buffer = new Memory<byte>(new byte[BlobDecoder.MaxBlobDataSize]);
        try
        {
            while (!token.IsCancellationRequested)
            {
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
                                await _outputChannel.Writer.WriteAsync(batch, token);
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
}
