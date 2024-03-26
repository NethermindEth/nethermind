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
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Abi;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using Nethermind.Consensus.AuRa.Config;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterP2P
{
    public static readonly ulong InstanceID = 0;
    public static readonly int Threshhold = 0;
    private readonly Action<Dto.DecryptionKeys> _onDecryptionKeysReceived;
    private readonly IReadOnlyBlockTree _readOnlyBlockTree;
    private readonly IReadOnlyTxProcessorSource _readOnlyTxProcessorSource;
    private readonly IAbiEncoder _abiEncoder;
    private readonly Address _keyBroadcastContractAddress;
    private readonly Address _keyperSetManagerContractAddress;

    public ShutterP2P(Action<Dto.DecryptionKeys> OnDecryptionKeysReceived, IReadOnlyBlockTree readOnlyBlockTree, IReadOnlyTxProcessorSource readOnlyTxProcessorSource, IAbiEncoder abiEncoder, IAuraConfig auraConfig)
    {
        _onDecryptionKeysReceived = OnDecryptionKeysReceived;
        _readOnlyBlockTree = readOnlyBlockTree;
        _readOnlyTxProcessorSource = readOnlyTxProcessorSource;
        _abiEncoder = abiEncoder;
        _keyBroadcastContractAddress = new(auraConfig.ShutterKeyBroadcastContractAddress);
        _keyperSetManagerContractAddress = new(auraConfig.ShutterKeyperSetManagerContractAddress);

        ServiceProvider serviceProvider = new ServiceCollection()
            .AddLibp2p(builder => builder)
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = "/shutter/0.1.0",
                AgentVersion = "github.com/shutter-network/rolling-shutter/rolling-shutter"
            })
            .BuildServiceProvider();

        IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;
        ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/" + auraConfig.ShutterP2PPort);
        Console.WriteLine(peer.Address);
        PubsubRouter router = serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = router.Subscribe("decryptionKeys");
        ConcurrentQueue<byte[]> msgQueue = new();

        topic.OnMessage += (byte[] msg) =>
        {
            if (BlockTreeIsReady())
            {
                msgQueue.Enqueue(msg);
            }
        };

        MyProto proto = new();
        CancellationTokenSource ts = new();
        _ = router.RunAsync(peer, proto, token: ts.Token);

        foreach (string addr in auraConfig.ShutterKeyperP2PAddresses)
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

                    IReadOnlyTransactionProcessor readOnlyTransactionProcessor = _readOnlyTxProcessorSource.Build(_readOnlyBlockTree.Head!.StateRoot!);
                    KeyBroadcastContract keyBroadcastContract = new(readOnlyTransactionProcessor, _abiEncoder, _keyBroadcastContractAddress);
                    KeyperSetManagerContract keyperSetManagerContract = new(readOnlyTransactionProcessor, _abiEncoder, _keyperSetManagerContractAddress);

                    if (!GetEonInfo(keyBroadcastContract, keyperSetManagerContract, out ulong eon, out Bls.P2 eonKey))
                    {
                        continue;
                    }

                    Dto.Envelope envelope = Dto.Envelope.Parser.ParseFrom(msg);
                    Dto.DecryptionKeys decryptionKeys = Dto.DecryptionKeys.Parser.ParseFrom(envelope.Message.ToByteString());
                    if (CheckDecryptionKeys(keyperSetManagerContract, decryptionKeys, eon, eonKey, Threshhold))
                    {
                        _onDecryptionKeysReceived(decryptionKeys);
                    }
                    else
                    {
                        throw new Exception("Invalid decryption keys received on P2P network.");
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

    internal bool CheckDecryptionKeys(IKeyperSetManagerContract keyperSetManagerContract, Dto.DecryptionKeys decryptionKeys, ulong eon, Bls.P2 eonKey, int threshold)
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
            Address keyperAddress = keyperSetManagerContract.GetKeyperSetAddress(_readOnlyBlockTree.Head!.Header, signerIndex).Item1;
            if (!ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(InstanceID, eon, slot, identities, signature.Span, keyperAddress))
            {
                return false;
            }
        }

        return true;
    }

    internal bool BlockTreeIsReady()
    {
        try
        {
            var _ = _readOnlyBlockTree.Head!.StateRoot!;
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    internal bool GetEonInfo(IKeyBroadcastContract keyBroadcastContract, IKeyperSetManagerContract keyperSetManagerContract, out ulong eon, out Bls.P2 eonKey)
    {
        eon = keyperSetManagerContract.GetNumKeyperSets(_readOnlyBlockTree.Head!.Header);
        byte[] eonKeyBytes = keyBroadcastContract.GetEonKey(_readOnlyBlockTree.Head!.Header, eon);

        // todo: remove once shutter fixes
        if (!eonKeyBytes.Any())
        {
            eonKeyBytes = Convert.FromHexString("2fdfb787563ac3aa9be365a581eae6684334cbb9ce11e95c486ea31820e0469a07a5e6e49caddee2b1891900848e7ed03749aac68d4d31d4f98f4a537b9050621a791a11c6c154ae972659a5a4ed7c55d2bf8772f1a4c05542436df59d0a2edc05ea7e70b72f27b4eb8a4fb5ed675cb35d67934a1ed75043ed3802ac6a8ed68c");
        }

        Console.WriteLine("eon: " + eon);
        Console.WriteLine("eon key: " + Convert.ToHexString(eonKeyBytes));

        eonKey = new(eonKeyBytes);
        return true;
    }
}
