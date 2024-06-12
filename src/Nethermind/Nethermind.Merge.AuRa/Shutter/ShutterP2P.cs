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
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterP2P
{
    private ShutterEonInfo _eonInfo;
    private readonly Func<Dto.DecryptionKeys, bool> _shouldProcessDecryptionKeys;
    private readonly Action<Dto.DecryptionKeys> _onDecryptionKeysValidated;
    private readonly IReadOnlyBlockTree _readOnlyBlockTree;
    private readonly ILogger _logger;
    private readonly ulong InstanceID;
    private readonly ConcurrentQueue<byte[]> _msgQueue = new();
    private PubsubRouter _router;
    private ILocalPeer _peer;

    public ShutterP2P(ShutterEonInfo eonInfo, Action<Dto.DecryptionKeys> onDecryptionKeysValidated, Func<Dto.DecryptionKeys, bool> shouldProcessDecryptionKeys, IReadOnlyBlockTree readOnlyBlockTree, IAuraConfig auraConfig, ILogManager logManager)
    {
        _eonInfo = eonInfo;
        _onDecryptionKeysValidated = onDecryptionKeysValidated;
        _shouldProcessDecryptionKeys = shouldProcessDecryptionKeys;
        _readOnlyBlockTree = readOnlyBlockTree;
        _logger = logManager.GetClassLogger();
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
        _peer = peerFactory.Create(new Identity(), "/ip4/0.0.0.0/tcp/" + auraConfig.ShutterP2PPort);
        if (_logger.IsInfo) _logger.Info($"Started Shutter P2P: {_peer.Address}");
        _router = serviceProvider.GetService<PubsubRouter>()!;

        ITopic topic = _router.Subscribe("decryptionKeys");

        topic.OnMessage += (byte[] msg) =>
        {
            _msgQueue.Enqueue(msg);
            if (_logger.IsDebug) _logger.Debug($"Received Shutter P2P message.");
        };

        Run(auraConfig.ShutterKeyperP2PAddresses);
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

    internal void Run(in IEnumerable<string> p2pAddresses)
    {
        MyProto proto = new();
        CancellationTokenSource ts = new();
        _ = _router.RunAsync(_peer, proto, token: ts.Token);
        ConnectToPeers(proto, p2pAddresses);

        long lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
        long delta = 0;

        Task.Run(() =>
        {
            for (; ; )
            {
                Thread.Sleep(250);

                while (_msgQueue.TryDequeue(out var msg))
                {
                    ProcessP2PMessage(msg);
                    lastMessageProcessed = DateTimeOffset.Now.ToUnixTimeSeconds();
                }

                long oldDelta = delta;
                delta = DateTimeOffset.Now.ToUnixTimeSeconds() - lastMessageProcessed;

                if (delta > 0 && delta % (60 * 5) == 0 && delta != oldDelta)
                {
                    if (_logger.IsWarn) _logger.Warn($"Not receiving Shutter messages ({delta / 60}m)...");
                }
            }
        });

        // todo: use cancellation source on finish
    }

    internal void ProcessP2PMessage(byte[] msg)
    {
        if (_logger.IsDebug) _logger.Debug($"Processing Shutter P2P message.");

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

        if (CheckDecryptionKeys(decryptionKeys))
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
            Bls.P1 dk, identity;
            try
            {
                dk = new(key.Key_.ToArray());
                identity = ShutterCrypto.ComputeIdentity(key.Identity.Span);
            }
            catch (ApplicationException e)
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: {e}.");
                return false;
            }

            if (!ShutterCrypto.CheckDecryptionKey(dk, _eonInfo.Key, identity))
            {
                if (_logger.IsDebug) _logger.Debug("Invalid decryption keys received on P2P network: decryption key did not match eon key.");
                return false;
            }
        }

        long signerIndicesCount = decryptionKeys.Gnosis.SignerIndices.LongCount();

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

        List<byte[]> identityPreimages = decryptionKeys.Keys.Select((Dto.Key key) => key.Identity.ToArray()).ToList();

        foreach ((ulong signerIndex, ByteString signature) in decryptionKeys.Gnosis.SignerIndices.Zip(decryptionKeys.Gnosis.Signatures))
        {
            Address keyperAddress = _eonInfo.Addresses[signerIndex];

            if (!ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(InstanceID, _eonInfo.Eon, decryptionKeys.Gnosis.Slot, decryptionKeys.Gnosis.TxPointer, identityPreimages, signature.Span, keyperAddress))
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: bad signature.");
                return false;
            }
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
