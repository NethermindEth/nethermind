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
using Nethermind.Abi;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Nethermind.Consensus.Processing;
using Nethermind.Evm.Precompiles.Bls;
using Nethermind.Core.Extensions;
using Microsoft.Extensions.Logging;
using ILogger = Nethermind.Logging.ILogger;

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
            // .AddLogging(builder =>
            //     builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace)
            //     .AddSimpleConsole(l =>
            //     {
            //         l.SingleLine = true;
            //         l.TimestampFormat = "[HH:mm:ss.FFF]";
            //     })
            // )
            .BuildServiceProvider();

        IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;
        ILocalPeer peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/" + auraConfig.ShutterP2PPort);
        if (_logger.IsInfo) _logger.Info($"Started Shutter P2P: {peer.Address}");
        PubsubRouter router = serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = router.Subscribe("decryptionKeys");
        ConcurrentQueue<byte[]> msgQueue = new();

        topic.OnMessage += msgQueue.Enqueue;

        MyProto proto = new();
        CancellationTokenSource ts = new();
        _ = router.RunAsync(peer, proto, token: ts.Token);
        ConnectToPeers(proto, auraConfig.ShutterKeyperP2PAddresses);

        long lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
        long delta = 0;
        long oldDelta = 0;
        Task.Run(() =>
        {
            for (; ; )
            {
                try
                {
                    Thread.Sleep(20);
                    oldDelta = delta;
                    delta = DateTimeOffset.Now.ToUnixTimeSeconds() - lastMessageProcessed;

                    if (msgQueue.TryDequeue(out var msg))
                    {
                        lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
                        ProcessP2PMessage(msg);
                    }
                    else if (delta > 0 && delta % (60 * 5) == 0 && delta != oldDelta)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Not receiving Shutter messages ({delta / 60}m)...");
                    }
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error("Shutter processing thread exception: " + e.Message);
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
            if (_logger.IsDebug) _logger.Debug("Could not parse Shutter decryption keys...");
            return;
        }

        if (!_shouldProcessDecryptionKeys(decryptionKeys))
        {
            return;
        }

        if (_eonInfo.Update() && CheckDecryptionKeys(decryptionKeys))
        {
            if (_logger.IsInfo) _logger.Info($"Validated Shutter decryption key for slot {decryptionKeys.Gnosis.Slot}");
            _onDecryptionKeysValidated(decryptionKeys);
        }
    }

    internal bool CheckDecryptionKeys(in Dto.DecryptionKeys decryptionKeys)
    {
        if (_logger.IsDebug) _logger.Debug($"Checking decryption keys instanceID: {decryptionKeys.InstanceID} eon: {decryptionKeys.Eon} #keys: {decryptionKeys.Keys.Count()} #sig: {decryptionKeys.Gnosis.Signatures.Count()} #txpointer: {decryptionKeys.Gnosis.TxPointer} #slot: {decryptionKeys.Gnosis.Slot} #block: {_readOnlyBlockTree.Head!.Header.Number}");

        if (decryptionKeys.InstanceID != InstanceID)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: instanceID {decryptionKeys.InstanceID} did not match expected value {InstanceID}.");
            return false;
        }

        if (decryptionKeys.Eon != _eonInfo.Eon)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: eon {decryptionKeys.Eon} did not match expected value {_eonInfo.Eon}.");
            return false;
        }

        foreach (Dto.Key key in decryptionKeys.Keys.AsEnumerable().Skip(1))
        {
            Bls.P1 dk = new(key.Key_.ToArray());
            Bls.P1 identity = ShutterCrypto.ComputeIdentity(key.Identity.Span);
            if (!ShutterCrypto.CheckDecryptionKey(dk, _eonInfo.Key, identity))
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: decryption key did not match eon key.");
                return false;
            }
        }

        int signerIndicesCount = decryptionKeys.Gnosis.SignerIndices.Count();

        if (decryptionKeys.Gnosis.SignerIndices.Distinct().Count() != signerIndicesCount)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: incorrect number of signer indices.");
            return false;
        }

        if (decryptionKeys.Gnosis.Signatures.Count() != signerIndicesCount)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: incorrect number of signatures.");
            return false;
        }

        if (signerIndicesCount != (int)_eonInfo.Threshold)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: signer indices did not match threshold.");
            return false;
        }

        IEnumerable<byte[]> identityPreimages = decryptionKeys.Keys.Skip(1).Select((Dto.Key key) => key.Identity.ToArray());

        foreach ((ulong signerIndex, ByteString signature) in decryptionKeys.Gnosis.SignerIndices.Zip(decryptionKeys.Gnosis.Signatures))
        {
            Address keyperAddress = _eonInfo.Addresses[signerIndex];
            // if (!ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(InstanceID, _eonInfo.Eon, decryptionKeys.Gnosis.Slot, identityPreimages, signature.Span, keyperAddress))
            // {
            //     if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: bad signature.");
            //     return false;
            // }
        }

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
