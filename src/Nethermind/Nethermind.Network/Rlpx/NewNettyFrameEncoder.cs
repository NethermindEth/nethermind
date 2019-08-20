using System;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class NewNettyFrameEncoder : MessageToByteEncoder<IByteBuffer>
    {
        private readonly ILogger _logger;
        private readonly IFrameCipher _frameCipher;
        private readonly IFrameMacProcessor _frameMacProcessor;
        
        public NewNettyFrameEncoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogger logger)
        {
            _frameCipher = frameCipher;
            _frameMacProcessor = frameMacProcessor;
            _logger = logger;
        }

        byte[] _encryptBuffer = new byte[16];
        byte[] _macBuffer = new byte[16];
        
        protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
        {
            if (input.ReadableBytes % 16 != 0)
            {
                throw new InvalidOperationException($"Frame length should be a multiple of 16");
            }

            if (output.WritableBytes < input.ReadableBytes)
            {
                output.DiscardReadBytes();
            }
            
            input.ReadBytes(_encryptBuffer);
            _frameCipher.Encrypt(_encryptBuffer, 0, 16, _encryptBuffer, 0);
            output.WriteBytes(_encryptBuffer);
            
            _frameMacProcessor.AddMac(_encryptBuffer, 0, 16, _macBuffer, 0, true);
            input.SkipBytes(16);
            output.WriteBytes(_macBuffer);

            int readableBytes = input.ReadableBytes;
            for (int i = 0; i < readableBytes / 16 - 1; i++)
            {
                input.ReadBytes(_encryptBuffer);
                _frameCipher.Encrypt(_encryptBuffer, 0, 16, _encryptBuffer, 0);
                _frameMacProcessor.EgressUpdate(_encryptBuffer);
                output.WriteBytes(_encryptBuffer);    
            }
            
            _frameMacProcessor.CalculateMac(_macBuffer);
            
            input.SkipBytes(16);
            output.WriteBytes(_macBuffer);
        }
    }
}