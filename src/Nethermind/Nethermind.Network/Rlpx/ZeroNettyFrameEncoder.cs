using System;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class ZeroNettyFrameEncoder : MessageToByteEncoder<IByteBuffer>
    {
        private readonly ILogger _logger;
        private readonly IFrameCipher _frameCipher;
        private readonly IFrameMacProcessor _frameMacProcessor;
        
        public ZeroNettyFrameEncoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogManager logManager)
        {
            _frameCipher = frameCipher ?? throw new ArgumentNullException(nameof(frameCipher));
            _frameMacProcessor = frameMacProcessor ?? throw new ArgumentNullException(nameof(frameMacProcessor));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private byte[] _encryptBuffer = new byte[16];
        private byte[] _macBuffer = new byte[16];
        
        protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
        {
            if (input.ReadableBytes % 16 != 0)
            {
                throw new InvalidOperationException($"Frame length should be a multiple of 16");
            }
            
            output.MakeSpace(input.ReadableBytes, "encoder");

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