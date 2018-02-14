using System;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Network.Rlpx.Handshake;
using Org.BouncyCastle.Crypto.Digests;

namespace Nevermind.Network.Rlpx
{
    public class NettyHandshakeHandler : ChannelHandlerAdapter
    {
        private readonly IByteBuffer _buffer = Unpooled.Buffer(256);
        private readonly PublicKey _remoteId;
        private readonly EncryptionHandshakeRole _role;

        private readonly IEncryptionHandshakeService _service;
        private readonly EncryptionHandshake _handshake = new EncryptionHandshake();

        // TODO: logger
        public NettyHandshakeHandler(IEncryptionHandshakeService service, EncryptionHandshakeRole role, PublicKey remoteId)
        {
            _handshake.RemotePublicKey = remoteId;
            _role = role;
            _remoteId = remoteId;
            _service = service;
        }

        public override void ChannelActive(IChannelHandlerContext context)
        {
            if (_role == EncryptionHandshakeRole.Initiator)
            {
                Packet auth = _service.Auth(_remoteId, _handshake);

                Console.WriteLine($"Sending AUTH ({auth.Data.Length})");
                _buffer.WriteBytes(auth.Data);
                Console.WriteLine(new Hex(auth.Data));
                context.WriteAndFlushAsync(_buffer);
            }
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            Console.WriteLine($"{exception}");
            base.ExceptionCaught(context, exception);
        }

        public override void ChannelReadComplete(IChannelHandlerContext context)
        {
            context.Flush();
        }

        public override void ChannelRead(IChannelHandlerContext context, object message)
        {
            Console.WriteLine($"Channel read on {context.Channel.LocalAddress}");

            if (message is IByteBuffer byteBuffer)
            {
                if (_role == EncryptionHandshakeRole.Recipient)
                {
                    Console.WriteLine("AUTH received");
                    byte[] authData = new byte[byteBuffer.MaxCapacity];
                    byteBuffer.ReadBytes(authData);
                    Console.WriteLine($"AUTH read ({authData.Length})");
                    Console.WriteLine(new Hex(authData));
                    Packet ack = _service.Ack(_handshake, new Packet(authData));
                    
                    Console.WriteLine($"Sending ACK ({ack.Data.Length})");
                    Console.WriteLine(new Hex(ack.Data));
                    _buffer.WriteBytes(ack.Data);
                    context.WriteAndFlushAsync(_buffer);
                    
                    LogSecrets();
                }
                else
                {
                    Console.WriteLine("ACK received");
                    byte[] ackData = new byte[byteBuffer.MaxCapacity];
                    byteBuffer.ReadBytes(ackData);
                    Console.WriteLine($"ACK read ({ackData.Length})");
                    Console.WriteLine(new Hex(ackData));
                    _service.Agree(_handshake, new Packet(ackData));

                    LogSecrets();
                    // TODO: clear pipeline, initiate protocol handshake (P2P)
                }
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        private void LogSecrets()
        {
            Console.WriteLine($"********* {_role} *********");
            Console.WriteLine($"{_role} AES secret:\t" + new Hex(_handshake.Secrets.AesSecret));
            Console.WriteLine($"{_role} MAC secret:\t" + new Hex(_handshake.Secrets.MacSecret));
            Console.WriteLine($"{_role} Token:\t" + new Hex(_handshake.Secrets.Token));

            byte[] ingressMac = new byte[32];
            new KeccakDigest(_handshake.Secrets.IngressMac).DoFinal(ingressMac, 0);
            Console.WriteLine($"{_role} Ingress MAC:\t" + new Hex(ingressMac));

            byte[] egressMac = new byte[32];
            new KeccakDigest(_handshake.Secrets.EgressMac).DoFinal(egressMac, 0);
            Console.WriteLine($"{_role} Egress MAC:\t" + new Hex(egressMac));
        }
    }
}