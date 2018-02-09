using System;
using System.Collections.Generic;
using System.Threading;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Network
{
    public class FrameSplittingProcessor : MessageProcessorBase<byte[], Packet>
    {
        // TODO: dynamic framing
        public const int FrameBoundary = 16;

        public const int MaxFrameSize = FrameBoundary * 64;
        private int _contextId;

        public override void ToRight(byte[] input, IList<Packet> output)
        {
            throw new NotImplementedException();
        }

        public override void ToLeft(Packet input, IList<byte[]> output)
        {
            Interlocked.Increment(ref _contextId);

            byte[] padded = Pad16(input.Data);
            int framesCount = padded.Length / MaxFrameSize + 1;
            
            for (int i = 0; i < framesCount; i++)
            {
                // TODO: rlp into existing array
                byte[] packetTypeData = i == 0 ? Rlp.Encode(input.PacketType ?? 0).Bytes : Bytes.Empty; // TODO: check the 0 packet type
                int packetTypeSize = packetTypeData.Length;
                
                int payloadOffset = MaxFrameSize * i;
                int dataSize = Math.Min(MaxFrameSize, padded.Length - payloadOffset);

                byte[] frame = new byte[16 + 16 + packetTypeData.Length + dataSize + 16]; // header + header MAC + packet type + payload + frame MAC

                frame[0] = (byte)(dataSize + packetTypeSize >> 16);
                frame[1] = (byte)(dataSize + packetTypeSize >> 8);
                frame[2] = (byte)(dataSize + packetTypeSize);
                List<object> headerDataItems = new List<object>();
                headerDataItems.Add(input.ProtocolType);
                if (framesCount > 1)
                {
                    headerDataItems.Add(_contextId);
                    if (i == 0)
                    {
                        headerDataItems.Add(packetTypeData.Length + padded.Length);
                    }
                }

                // TODO: rlp into existing array
                byte[] headerDataBytes = Rlp.Encode(headerDataItems).Bytes;

                Buffer.BlockCopy(headerDataBytes, 0, frame, 3, headerDataBytes.Length);
                Buffer.BlockCopy(packetTypeData, 0, frame, 32, packetTypeSize);
                Buffer.BlockCopy(padded, payloadOffset, frame, 32 + packetTypeSize, dataSize);

                output.Add(frame);
            }
        }

        private static byte[] Pad16(byte[] data)
        {
            int paddingSize = 16 - data.Length % 16;
            byte[] padded = paddingSize == 16 ? data : Bytes.Concat(data, new byte[paddingSize]);
            return padded;
        }
    }
}