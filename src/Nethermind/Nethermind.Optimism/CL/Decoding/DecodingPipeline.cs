// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Optimism.CL.Decoding;

public class DecodingPipeline : IDecodingPipeline
{
    private readonly Task _mainTask;
    private readonly ILogger _logger;

    private readonly IChannelStorage _channelStorage = new ChannelStorage();

    public DecodingPipeline(ILogger logger)
    {
        _logger = logger;
        _mainTask = new(async () =>
        {
            // TODO: cancellation
            while (true)
            {
                byte[] blob = await _inputChannel.Reader.ReadAsync();
                byte[] data = BlobDecoder.DecodeBlob(blob);
                Frame[] frames = FrameDecoder.DecodeFrames(data);
                _channelStorage.ConsumeFrames(frames);
                BatchV1[]? batches = _channelStorage.GetReadyBatches();
                if (batches is not null)
                {
                    foreach (BatchV1 batch in batches)
                    {
                        await _outputChannel.Writer.WriteAsync(batch);
                    }
                }
            }
        });
    }

    private readonly Channel<byte[]> _inputChannel = Channel.CreateBounded<byte[]>(20);
    private readonly Channel<BatchV1> _outputChannel = Channel.CreateBounded<BatchV1>(20);

    public ChannelWriter<byte[]> DaDataWriter => _inputChannel.Writer;
    public ChannelReader<BatchV1> DecodedBatchesReader => _outputChannel.Reader;

    public void Start()
    {
        _mainTask.Start();
    }
}
