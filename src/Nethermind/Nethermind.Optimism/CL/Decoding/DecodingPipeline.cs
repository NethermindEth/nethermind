// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Optimism.CL.Decoding;

public class DecodingPipeline : IDecodingPipeline
{
    private readonly Channel<byte[]> _inputChannel = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<BatchV1> _outputChannel = Channel.CreateUnbounded<BatchV1>();
    private readonly IChannelStorage _channelStorage = new ChannelStorage();

    private readonly Task _mainTask;
    private readonly ILogger _logger;

    public ChannelWriter<byte[]> DaDataWriter => _inputChannel.Writer;
    public ChannelReader<BatchV1> DecodedBatchesReader => _outputChannel.Reader;

    public DecodingPipeline(CancellationToken token, ILogger logger)
    {
        _logger = logger;
        _mainTask = MainLoop(token);
    }

    public void Start()
    {
        _mainTask.Start();
    }

    private async Task MainLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // TODO: a lot of allocations here
            byte[] blob = await _inputChannel.Reader.ReadAsync(token);
            byte[] data = BlobDecoder.DecodeBlob(blob);
            Frame[] frames = FrameDecoder.DecodeFrames(data);
            _channelStorage.ConsumeFrames(frames);
            BatchV1[]? batches = _channelStorage.GetReadyBatches();
            if (batches is not null)
            {
                foreach (BatchV1 batch in batches)
                {
                    await _outputChannel.Writer.WriteAsync(batch, token);
                }
            }
        }
    }
}
