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

        protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
        {
            if (input.ReadableBytes % 16 != 0)
            {
                throw new InvalidOperationException($"Frame length should be a multiple of 16");
            }

            input.ReadBytes(output, input.ReadableBytes);
//
//            // for now
//            input.ReadBytes(_read16);
//            input.SkipBytes(16);
//            
////            if(_logger.IsTrace) _logger.Trace($"Sending frame (before encryption): {message.ToHexString()}");
//            _frameCipher.Encrypt(_read16, 0, 16, _buffer16, 0);
//            output.WriteBytes(_buffer16);
//            
//            _frameMacProcessor.AddMac(_buffer16, 0, 16, true);
//            output.WriteBytes(_write16);
//
//            for (int i = 0; i < output.ReadableBytes / 16; i++)
//            {
//                input.ReadBytes(_read16);
//                _frameCipher.Encrypt(_read16, 0, 16, _write16,  0);
//                output.WriteBytes(_write16);
//            }
//            
////            _frameMacProcessor.AddMac(_write16, 0, 236, _write16, 0, false);
//            output.WriteBytes(_write16);
//            
////            if(_logger.IsTrace) _logger.Trace($"Sending frame (after encryption):  {message.ToHexString()}");
        }
    }
}