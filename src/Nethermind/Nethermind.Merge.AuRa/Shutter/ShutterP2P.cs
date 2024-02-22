using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Protocols;
using System;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Buffers.Binary;
using Nethermind.Libp2p.Core.Enums;
using Nethermind.Crypto;
using Multiaddr = Nethermind.Libp2p.Core.Multiaddr;
using Nethermind.Core;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterP2P
{
    public static readonly ulong InstanceId = 0;
    private readonly Action<DecryptionKeys> _onDecryptionKeysReceived;

    public ShutterP2P(Action<DecryptionKeys> OnDecryptionKeysReceived)
    {
        _onDecryptionKeysReceived = OnDecryptionKeysReceived;

        var key = File.ReadAllLines("/src/play/work/keyper-dkg-external/keyper-0.toml").First(l => l.StartsWith("P2PKey = ")).Replace("P2PKey = ", "").Trim('\'');

#pragma warning disable CA2252 // This API requires opting into preview features
        ServiceProvider serviceProvider = new ServiceCollection()
            .AddLibp2p(builder => builder)
            .AddSingleton(new Libp2p.Protocols.Identify.Dto.Identify() // IdentifySettings?
            {
                ProtocolVersion = "shutter/0.1.0"
            })
            .AddLogging(builder =>
                    builder.SetMinimumLevel(LogLevel.Trace)
                    .AddSimpleConsole(l =>
                    {
                        l.SingleLine = true;
                        l.TimestampFormat = "[HH:mm:ss.FFF]";
                    }))
        .BuildServiceProvider();
#pragma warning restore CA2252 // This API requires opting into preview features

        IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;
        ILocalPeer peer = peerFactory.Create(new Identity(Convert.FromBase64String(key)), "/ip4/127.0.0.1/tcp/23102");
        PubsubRouter router = serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = router.Subscribe("decryptionKey");
        topic.OnMessage += (byte[] msg) =>
        {
            DecryptionKeys decryptionKeys = ParseDecrpytionKeys(msg);
            if (CheckDecryptionKeys(decryptionKeys, 0, 0))
            {
                _onDecryptionKeysReceived(decryptionKeys);
            }
            else
            {
                // todo: don't do this in production!
                throw new Exception("Invalid decryption keys received on P2P network.");
            }
        };

        MyProto proto = new();
        CancellationTokenSource ts = new();
        _ = router.RunAsync(peer, proto, token: ts.Token);
    }

    public struct DecryptionKeys
    {
        public ulong InstanceId;
        public ulong Eon;
        public ulong Slot;
        public ulong TxPointer;
        public IEnumerable<(byte[], byte[])> Keys; // (identity, key)
        public IEnumerable<ulong> SignerIndices;
        public IEnumerable<byte[]> Signatures;
    }

    internal class MyProto : IDiscoveryProtocol
    {
        Func<Multiaddr[], bool>? IDiscoveryProtocol.OnAddPeer { set => throw new NotImplementedException(); }
        Func<Multiaddr[], bool>? IDiscoveryProtocol.OnRemovePeer { set => throw new NotImplementedException(); }

        public Task DiscoverAsync(Multiaddr localPeerAddr, CancellationToken token = default)
        {
            throw new NotImplementedException();
        }
    }

    internal DecryptionKeys ParseDecrpytionKeys(ReadOnlySpan<byte> bytes)
    {
        // todo: parse properly, SSZ encoding?
        return new()
        {
            InstanceId = BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]),
            Eon = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
            Slot = BinaryPrimitives.ReadUInt64BigEndian(bytes[16..]),
            TxPointer = BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]),
            Keys = [],
            SignerIndices = [],
            Signatures = [],
        };
    }

    internal bool CheckDecryptionKeys(DecryptionKeys decryptionKeys, ulong eon, int threshold)
    {
        Bls.P2 eonKey = new(); //todo: get this from a contract?
        ulong slot = 0;

        if (decryptionKeys.InstanceId != InstanceId || decryptionKeys.Eon != eon)
        {
            return false;
        }

        foreach ((byte[] key, byte[] identity) in decryptionKeys.Keys)
        {
            if (!ShutterCrypto.CheckDecryptionKey(new(key), eonKey, new(identity)))
            {
                return false;
            }
        }

        int signerIndicesCount = decryptionKeys.SignerIndices.Count();

        if (decryptionKeys.SignerIndices.Distinct().Count() != signerIndicesCount)
        {
            return false;
        }

        if (decryptionKeys.Signatures.Count() != signerIndicesCount)
        {
            return false;
        }

        if (signerIndicesCount != threshold)
        {
            return false;
        }

        IEnumerable<Bls.P1> identities = decryptionKeys.Keys.Select(((byte[], byte[]) x) => new Bls.P1(x.Item2));
        foreach ((ulong signerIndex, byte[] signature) in decryptionKeys.SignerIndices.Zip(decryptionKeys.Signatures))
        {
            // lookup keyper address with signer index?
            Address keyperAddress = Address.Zero;
            if (!ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(InstanceId, eon, slot, identities, signature, keyperAddress))
            {
                return false;
            }
        }

        return true;
    }
}
