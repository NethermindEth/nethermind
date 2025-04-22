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
    private readonly Channel<byte[]> _inputChannel = Channel.CreateBounded<byte[]>(9);
    private readonly Channel<BatchV1> _outputChannel = Channel.CreateBounded<BatchV1>(3);
    private readonly IFrameQueue _frameQueue = new FrameQueue(logger);

    public ChannelWriter<byte[]> DaDataWriter => _inputChannel.Writer;
    public ChannelReader<BatchV1> DecodedBatchesReader => _outputChannel.Reader;

    public async Task Run(CancellationToken token)
    {
        var buffer = new Memory<byte>(new byte[BlobDecoder.MaxBlobDataSize]);
        while (!token.IsCancellationRequested)
        {
            buffer.Clear();
            var blob = await _inputChannel.Reader.ReadAsync(token);

            try
            {
                var read = BlobDecoder.DecodeBlob(blob, buffer.Span);
                foreach (var frame in FrameDecoder.DecodeFrames(buffer[..read]))
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
                if (logger.IsWarn) logger.Warn($"Unhandled exception in decoding pipeline: {e}");
            }
        }
    }
}
