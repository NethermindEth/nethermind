// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Optimism.CL.Decoding;

public class DecodingPipeline : IDecodingPipeline
{
    private readonly Channel<byte[]> _inputChannel = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<BatchV1> _outputChannel = Channel.CreateUnbounded<BatchV1>();
    private readonly IChannelStorage _channelStorage = new ChannelStorage();
    private readonly ILogger _logger;

    public ChannelWriter<byte[]> DaDataWriter => _inputChannel.Writer;
    public ChannelReader<BatchV1> DecodedBatchesReader => _outputChannel.Reader;

    public DecodingPipeline(ILogger logger)
    {
        _logger = logger;
    }

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
                    _channelStorage.ConsumeFrame(frame);
                }

                var batches = _channelStorage.GetReadyBatches();
                if (batches is not null)
                {
                    foreach (var batch in batches)
                    {
                        await _outputChannel.Writer.WriteAsync(batch, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {

            }
            catch (Exception e)
            {
                if (_logger.IsWarn) _logger.Warn($"Unhandled exception in decoding pipeline: {e}");
            }
        }
    }
}
