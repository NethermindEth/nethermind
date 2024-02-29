using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Stack;
using Nethermind.Libp2p.Protocols;
using System;
using System.Threading.Tasks;
using System.Threading;
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
    private readonly INethermindApi _api;

    public ShutterP2P(Action<Dto.DecryptionKeys> OnDecryptionKeysReceived, Contracts.IKeyBroadcastContract keyBroadcastContract, Contracts.IKeyperSetManagerContract keyperSetManagerContract, INethermindApi api)
    {
        _onDecryptionKeysReceived = OnDecryptionKeysReceived;
        _keyBroadcastContract = keyBroadcastContract;
        _keyperSetManagerContract = keyperSetManagerContract;
        _api = api;

        ServiceProvider serviceProvider = new ServiceCollection()
            .AddLibp2p(builder => builder)
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = "/shutter/0.1.0",
                AgentVersion = "github.com/shutter-network/rolling-shutter/rolling-shutter"
            })
            .BuildServiceProvider();

        IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;
        ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/23102");
        Console.WriteLine(peer.Address);
        PubsubRouter router = serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = router.Subscribe("decryptionKeys");
        topic.OnMessage += (byte[] msg) =>
        {
            ulong eon = _keyperSetManagerContract.GetNumKeyperSets(_api.BlockTree!.Head!.Header);
            Dto.Envelope envelope = Dto.Envelope.Parser.ParseFrom(msg);
            Dto.DecryptionKeys decryptionKeys = Dto.DecryptionKeys.Parser.ParseFrom(envelope.Message.ToByteString());
            if (CheckDecryptionKeys(decryptionKeys, eon, Threshhold))
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

        // todo: don't hardcode
        proto.OnAddPeer?.Invoke(["/ip4/64.226.117.95/tcp/23000/p2p/12D3KooWDu1DQcEXyJRwbq6spG5gbi11MbN3iSSqbc2Z85z7a8jB"]);
        proto.OnAddPeer?.Invoke(["/ip4/64.226.117.95/tcp/23001/p2p/12D3KooWFbscPyxc3rxyoEgyLbDYpbfx6s6di5wnr4cFz77q3taH"]);
        proto.OnAddPeer?.Invoke(["/ip4/64.226.117.95/tcp/23002/p2p/12D3KooWLmDDaCkXZgkWUnWZ1RxLzA1FHm4cVHLnNvCuGi4haGLu"]);
        proto.OnAddPeer?.Invoke(["/ip4/64.226.117.95/tcp/23003/p2p/12D3KooW9y8s8gy52jHXvJXNU5D2HuDmXxrs5Kp4VznbiBtRUnU5"]);

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

    internal bool CheckDecryptionKeys(Dto.DecryptionKeys decryptionKeys, ulong eon, int threshold)
    {
        Bls.P2 eonKey = new(_keyBroadcastContract.GetEonKey(_api.BlockTree!.Head!.Header, eon));
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
            Address keyperAddress = _keyperSetManagerContract.GetKeyperSetAddress(_api.BlockTree!.Head.Header, signerIndex).Item1;
            if (!ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(InstanceID, eon, slot, identities, signature.Span, keyperAddress))
            {
                return false;
            }
        }

        return true;
    }
}
