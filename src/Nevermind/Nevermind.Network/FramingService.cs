using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Network
{
    public class FramingService : IFramingService
    {
        public const int FrameBoundary = 16;
        public const int MaxFrameSize = FrameBoundary * 64;

        public byte[] Package(int protocolType, int packetType, byte[] data)
        {
            return Package(protocolType, packetType, null, data);
        }

        public byte[] Package(int protocolType, int packetType, int? contextId, byte[] data)
        {
            byte[] padded = Pad16(data);
            int framesCount = padded.Length / MaxFrameSize + 1;
            if (framesCount > 1)
            {
                Debug.Assert(contextId.HasValue, "Context ID expected when in multi-frame packet");
            }

            byte[][] chunks = new byte[framesCount][];
            byte[] packetTypeData = Rlp.Encode(packetType).Bytes;
            for (int i = 0; i < framesCount; i++)
            {
                int offset = MaxFrameSize * i;
                int dataSize = Math.Min(MaxFrameSize, padded.Length - offset);
                byte[] frame = padded.Slice(offset, dataSize);
                if (i == 0)
                {
                    frame = Bytes.Concat(packetTypeData, frame);
                }

                byte[] header = new byte[32];
                header[0] = (byte)(frame.Length >> 16);
                header[1] = (byte)(frame.Length >> 8);
                header[2] = (byte)frame.Length;
                List<object> headerDataItems = new List<object>();
                headerDataItems.Add(protocolType);
                if (framesCount > 1)
                {
                    headerDataItems.Add(contextId.Value);
                    if (i == 0)
                    {
                        headerDataItems.Add(packetTypeData.Length + padded.Length);
                    }
                }

                int b = 10;
                long a = (long)b;
                
                chunks[i] = Bytes.Concat(
                    Pad16(Rlp.Encode(headerDataItems).Bytes),
                    new byte[16],
                    frame,
                    new byte[16]);
            }

            return Bytes.Concat(chunks);
        }

        private static byte[] Pad16(byte[] data)
        {
            int paddingSize = 16 - data.Length % 16;
            byte[] padded = paddingSize == 16 ? data : Bytes.Concat(data, new byte[paddingSize]);
            return padded;
        }
    }
}