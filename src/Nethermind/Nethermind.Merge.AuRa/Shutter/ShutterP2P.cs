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
using Google.Protobuf;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Abi;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Consensus.Processing;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterP2P
{
    private readonly Func<Dto.DecryptionKeys, bool> _shouldProcessDecryptionKeys;
    private readonly Action<Dto.DecryptionKeys> _onDecryptionKeysValidated;
    private readonly IReadOnlyBlockTree _readOnlyBlockTree;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ILogger _logger;
    private readonly ulong InstanceID;
    private ShutterEonInfo _eonInfo;

    public ShutterP2P(Action<Dto.DecryptionKeys> onDecryptionKeysValidated, Func<Dto.DecryptionKeys, bool> shouldProcessDecryptionKeys, IReadOnlyBlockTree readOnlyBlockTree, ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory, IAbiEncoder abiEncoder, IAuraConfig auraConfig, ILogManager logManager)
    {
        _onDecryptionKeysValidated = onDecryptionKeysValidated;
        _shouldProcessDecryptionKeys = shouldProcessDecryptionKeys;
        _readOnlyBlockTree = readOnlyBlockTree;
        _abiEncoder = abiEncoder;
        _logger = logManager.GetClassLogger();
        _eonInfo = new(readOnlyBlockTree, readOnlyTxProcessingEnvFactory, abiEncoder, auraConfig, _logger);
        InstanceID = auraConfig.ShutterInstanceID;

        ServiceProvider serviceProvider = new ServiceCollection()
            .AddLibp2p(builder => builder)
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = auraConfig.ShutterP2PProtocolVersion,
                AgentVersion = auraConfig.ShutterP2PAgentVersion
            })
            .BuildServiceProvider();

        IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;
        ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/" + auraConfig.ShutterP2PPort);
        if (_logger.IsInfo) _logger.Info($"Started Shutter P2P: {peer.Address}");
        PubsubRouter router = serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = router.Subscribe("decryptionKeys");
        ConcurrentQueue<byte[]> msgQueue = new();

        long msgCount = 0;
        topic.OnMessage += (byte[] msg) =>
        {
            Interlocked.Increment(ref msgCount);
            msgQueue.Enqueue(msg);
        };

        MyProto proto = new();
        CancellationTokenSource ts = new();
        _ = router.RunAsync(peer, proto, token: ts.Token);
        ConnectToPeers(proto, auraConfig.ShutterKeyperP2PAddresses);

        long lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
        long backoff = 10;
        Task.Run(() =>
        {
            for (; ; )
            {
                try
                {
                    Thread.Sleep(20);
                    long delta = DateTimeOffset.Now.ToUnixTimeSeconds() - lastMessageProcessed;

                    if (msgQueue.TryDequeue(out var msg))
                    {
                        ProcessP2PMessage(msg);
                        lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
                        backoff = 10;
                    }
                    else if (delta >= backoff)
                    {
                        if (_logger.IsWarn) _logger.Warn("Not receiving Shutter messages, reconnecting...");
                        ConnectToPeers(proto, auraConfig.ShutterKeyperP2PAddresses);
                        lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
                        backoff *= 2;
                    }
                }
                catch (Exception e)
                {
                    _logger.Error("Shutter processing thread exception: " + e.Message);
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

    internal void ProcessP2PMessage(byte[] msg)
    {

        Dto.Envelope envelope = Dto.Envelope.Parser.ParseFrom(msg);
        if (!envelope.Message.TryUnpack(out Dto.DecryptionKeys decryptionKeys))
        {
            if (_logger.IsWarn) _logger.Warn("Could not parse Shutter decryption keys...");
            return;
        }

        if (!_shouldProcessDecryptionKeys(decryptionKeys))
        {
            return;
        }

        _eonInfo.Update();

        if (CheckDecryptionKeys(decryptionKeys))
        {
            if (_logger.IsInfo) _logger.Info($"Validated Shutter decryption key for slot {decryptionKeys.Gnosis.Slot}");
            _onDecryptionKeysValidated(decryptionKeys);
        }
    }

    internal bool CheckDecryptionKeys(in Dto.DecryptionKeys decryptionKeys)
    {
        if (_logger.IsInfo) _logger.Info($"Checking decryption keys instanceID: {decryptionKeys.InstanceID} eon: {decryptionKeys.Eon} #keys: {decryptionKeys.Keys.Count()} #sig: {decryptionKeys.Gnosis.Signatures.Count()} #txpointer: {decryptionKeys.Gnosis.TxPointer} #slot: {decryptionKeys.Gnosis.Slot} #block: {_readOnlyBlockTree.Head!.Header.Number}");

        if (decryptionKeys.InstanceID != InstanceID)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid decryption keys received on P2P network: instanceID {decryptionKeys.InstanceID} did not match expected value {InstanceID}.");
            return false;
        }

        if (decryptionKeys.Eon != _eonInfo.Eon)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid decryption keys received on P2P network: eon {decryptionKeys.Eon} did not match expected value {_eonInfo.Eon}.");
            return false;
        }

        // todo: enable when Shutter uses BLS
        // foreach (Dto.Key key in decryptionKeys.Keys.AsEnumerable())
        // {
        //    if (!ShutterCrypto.CheckDecryptionKey(new(key.Key_.ToArray()), eonKey, new(key.Identity.ToArray())))
        //     {
        //         return false;
        //     }
        // }

        int signerIndicesCount = decryptionKeys.Gnosis.SignerIndices.Count();

        if (decryptionKeys.Gnosis.SignerIndices.Distinct().Count() != signerIndicesCount)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid decryption keys received on P2P network: incorrect number of signer indices.");
            return false;
        }

        if (decryptionKeys.Gnosis.Signatures.Count() != signerIndicesCount)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid decryption keys received on P2P network: incorrect number of signatures.");
            return false;
        }

        if (signerIndicesCount != (int)_eonInfo.Threshold)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid decryption keys received on P2P network: signer indices did not match threshold.");
            return false;
        }

        // IEnumerable<Bls.P1> identities = decryptionKeys.Keys.Select((Dto.Key key) => new Bls.P1(key.Identity.ToArray()));

        // foreach ((ulong signerIndex, ByteString signature) in decryptionKeys.Gnosis.SignerIndices.Zip(decryptionKeys.Gnosis.Signatures))
        // {
        //     Address keyperAddress = _eonInfo.Addresses[signerIndex];
        //     if (!ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(InstanceID, eon, slot, identities, signature.Span, keyperAddress))
        //     {
        //         return false;
        //     }
        // }

        return true;
    }

    internal void ConnectToPeers(MyProto proto, IEnumerable<string> p2pAddresses)
    {
        foreach (string addr in p2pAddresses)
        {
            proto.OnAddPeer?.Invoke([addr]);
        }
    }
}
