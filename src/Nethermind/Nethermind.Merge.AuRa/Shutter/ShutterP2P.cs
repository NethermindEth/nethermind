using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Protocols;
using Nethermind.Blockchain;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Collections.Generic;
using Nethermind.Crypto;
using Multiformats.Address;
using Nethermind.Core;
using Nethermind.Api;
using Google.Protobuf;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterP2P
{
    public static readonly ulong InstanceID = 0;
    public static readonly int Threshhold = 0;
    private readonly Action<Dto.DecryptionKeys> _onDecryptionKeysReceived;
    private readonly Contracts.IKeyBroadcastContract _keyBroadcastContract;
    private readonly Contracts.IKeyperSetManagerContract _keyperSetManagerContract;
    private readonly IReadOnlyBlockTree _readOnlyBlockTree;

    public ShutterP2P(Action<Dto.DecryptionKeys> OnDecryptionKeysReceived, Contracts.IKeyBroadcastContract keyBroadcastContract, Contracts.IKeyperSetManagerContract keyperSetManagerContract, IReadOnlyBlockTree readOnlyBlockTree, string[] keyperAddresses, string port)
    {
        _onDecryptionKeysReceived = OnDecryptionKeysReceived;
        _keyBroadcastContract = keyBroadcastContract;
        _keyperSetManagerContract = keyperSetManagerContract;
        _readOnlyBlockTree = readOnlyBlockTree;

        ServiceProvider serviceProvider = new ServiceCollection()
            .AddLibp2p(builder => builder)
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = "/shutter/0.1.0",
                AgentVersion = "github.com/shutter-network/rolling-shutter/rolling-shutter"
            })
            .BuildServiceProvider();

        IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;
        ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/" + port);
        Console.WriteLine(peer.Address);
        PubsubRouter router = serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = router.Subscribe("decryptionKeys");
        ConcurrentQueue<byte[]> msgQueue = new();

        topic.OnMessage += (byte[] msg) =>
        {
            // wait until head is synced???
            if (BlockTreeIsReady())
            {
                msgQueue.Enqueue(msg);
            }
        };

        MyProto proto = new();
        CancellationTokenSource ts = new();
        _ = router.RunAsync(peer, proto, token: ts.Token);

        foreach (string addr in keyperAddresses)
        {
            proto.OnAddPeer?.Invoke([addr]);
        }

        Task.Run(() =>
        {
            for (; ; )
            {
                Thread.Yield();
                if (BlockTreeIsReady() && msgQueue.TryDequeue(out var msg))
                {
                    Console.WriteLine("processing... " + Convert.ToHexString(msg));

                    lock (_readOnlyBlockTree)
                    {
                        if (!GetEonInfo(out ulong eon, out Bls.P2 eonKey))
                        {
                            continue;
                        }

                        Dto.Envelope envelope = Dto.Envelope.Parser.ParseFrom(msg);
                        Dto.DecryptionKeys decryptionKeys = Dto.DecryptionKeys.Parser.ParseFrom(envelope.Message.ToByteString());
                        if (CheckDecryptionKeys(decryptionKeys, eon, eonKey, Threshhold))
                        {
                            _onDecryptionKeysReceived(decryptionKeys);
                        }
                        else
                        {
                            throw new Exception("Invalid decryption keys received on P2P network.");
                        }
                    }
                }
            }
        });
    }

    internal class MyProto : IDiscoveryProtocol
    {
        public Func<Multiaddress[], bool>? OnAddPeer { get; set; }
        public Func<Multiaddress[], bool>? OnRemovePeer { get; set; }

        public Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default)
        {
            return Task.Delay(int.MaxValue);
        }
    }

    internal bool CheckDecryptionKeys(Dto.DecryptionKeys decryptionKeys, ulong eon, Bls.P2 eonKey, int threshold)
    {
        ulong slot = 0;

        if (decryptionKeys.InstanceID != InstanceID || decryptionKeys.Eon != eon)
        {
            return false;
        }

        foreach (Dto.Key key in decryptionKeys.Keys.AsEnumerable())
        {
            if (!ShutterCrypto.CheckDecryptionKey(new(key.Key_.ToArray()), eonKey, new(key.Identity.ToArray())))
            {
                return false;
            }
        }

        int signerIndicesCount = decryptionKeys.Gnosis.SignerIndices.Count();

        if (decryptionKeys.Gnosis.SignerIndices.Distinct().Count() != signerIndicesCount)
        {
            return false;
        }

        if (decryptionKeys.Gnosis.Signatures.Count() != signerIndicesCount)
        {
            return false;
        }

        if (signerIndicesCount != threshold)
        {
            return false;
        }

        IEnumerable<Bls.P1> identities = decryptionKeys.Keys.Select((Dto.Key key) => new Bls.P1(key.Identity.ToArray()));

        foreach ((ulong signerIndex, ByteString signature) in decryptionKeys.Gnosis.SignerIndices.Zip(decryptionKeys.Gnosis.Signatures))
        {
            Address keyperAddress = _keyperSetManagerContract.GetKeyperSetAddress(_readOnlyBlockTree.Head!.Header, signerIndex).Item1;
            if (!ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(InstanceID, eon, slot, identities, signature.Span, keyperAddress))
            {
                return false;
            }
        }

        return true;
    }

    internal bool BlockTreeIsReady()
    {
        return _readOnlyBlockTree.Head is not null && !_readOnlyBlockTree.Head.IsGenesis;
    }

    internal bool GetEonInfo(out ulong eon, out Bls.P2 eonKey)
    {
        eon = _keyperSetManagerContract.GetNumKeyperSets(_readOnlyBlockTree.Head!.Header);
        byte[] eonKeyBytes = _keyBroadcastContract.GetEonKey(_readOnlyBlockTree.Head!.Header, eon);

        Console.WriteLine("eon: " + eon);
        Console.WriteLine("eon key: " + Convert.ToHexString(eonKeyBytes));

        if (!eonKeyBytes.Any())
        {
            Console.WriteLine("no eon key");
            eonKey = new();
            return false;
        }

        eonKey = new(eonKeyBytes);
        return true;
    }
}
