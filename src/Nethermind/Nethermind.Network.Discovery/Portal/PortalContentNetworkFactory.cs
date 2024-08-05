// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.WireProtocol;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

/// <summary>
/// A "portal content network" is basically a kademlia network on top of discv5 subprotocol via TalkReq with the
/// addition of using UTP (as another discv5 subprotocol) when transferring large binary.
/// This class basically, wire all those things up into an IPortalContentNetwork.
/// The specific interpretation of the key and content is handle at higher level, see `PortalHistoryNetwork` for history network.
/// </summary>
/// <param name="enrProvider"></param>
/// <param name="talkReqTransport"></param>
/// <param name="utpManager"></param>
/// <param name="logManager"></param>
public class PortalContentNetworkFactory(
    IEnrProvider enrProvider,
    ITalkReqTransport talkReqTransport,
    IUtpManager utpManager,
    ILogManager logManager)
    : IPortalContentNetworkFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<PortalContentNetworkFactory>();
    private readonly EnrNodeHashProvider _nodeHashProvider = EnrNodeHashProvider.Instance;

    public IPortalContentNetwork Create(byte[] protocol, IPortalContentNetwork.Store store)
    {
        var messageSender = new KademliaTalkReqMessageSender(protocol, talkReqTransport, enrProvider, logManager);
        var kadstore = new PortalContentStoreAdapter(store);
        var kademlia = new Kademlia<IEnr, byte[], LookupContentResult>(
            _nodeHashProvider,
            kadstore,
            messageSender,
            logManager,
            enrProvider.SelfEnr,
            20,
            3,
            TimeSpan.FromHours(1)
        );
        var contentDistributor = new ContentDistributor(kademlia, protocol, talkReqTransport, utpManager);
        talkReqTransport.RegisterProtocol(protocol, new KademliaTalkReqHandler(
            store,
            kademlia,
            contentDistributor,
            enrProvider.SelfEnr,
            utpManager));

        return new PortalContentNetwork(utpManager, kademlia, messageSender, contentDistributor, _logger);
    }

    private class PortalContentStoreAdapter(IPortalContentNetwork.Store sourceStore) : IKademlia<IEnr, byte[], LookupContentResult>.IStore
    {
        public bool TryGetValue(byte[] contentId, out LookupContentResult? value)
        {
            byte[]? sourceContent = sourceStore.GetContent(contentId);
            if (sourceContent == null)
            {
                value = null;
                return false;
            }

            value = new LookupContentResult()
            {
                Payload = sourceContent
            };
            return true;
        }
    }

    public class PortalContentNetwork(
        IUtpManager utpManager,
        IKademlia<IEnr, byte[], LookupContentResult> kademlia,
        IMessageSender<IEnr, byte[], LookupContentResult> messageSender,
        IContentDistributor contentDistributor,
        ILogger logger)
        : IPortalContentNetwork
    {

        public async Task<byte[]?> LookupContent(byte[] key, CancellationToken token)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            token = cts.Token;

            Stopwatch sw = Stopwatch.StartNew();
            var result = await kademlia.LookupValue(key, token);
            logger.Info($"Lookup {key.ToHexString()} took {sw.Elapsed}");

            sw.Restart();

            if (result == null) return null;

            if (result.Payload != null) return result.Payload;

            Debug.Assert(result.ConnectionId != null);

            MemoryStream stream = new MemoryStream();
            await utpManager.ReadContentFromUtp(result.NodeId, true, result.ConnectionId.Value, stream, token);
            var asBytes = stream.ToArray();
            logger.Info($"UTP download for {key.ToHexString()} took {sw.Elapsed}");
            return asBytes;
        }

        public async Task<byte[]?> LookupContentFrom(IEnr node, byte[] contentKey, CancellationToken token)
        {
            var content = await messageSender.FindValue(node, contentKey, token);
            if (!content.hasValue)
            {
                return null;
            }

            var value = content.value!;
            if (value.Payload != null) return value.Payload;

            MemoryStream stream = new MemoryStream();
            await utpManager.ReadContentFromUtp(node, true, value.ConnectionId!.Value, stream, token);
            var asBytes = stream.ToArray();
            return asBytes;
        }

        public Task BroadcastContent(byte[] contentKey, byte[] value, CancellationToken token)
        {
            return contentDistributor.DistributeContent(contentKey, value, token);
        }

        public async Task Run(CancellationToken token)
        {
            await kademlia.Run(token);
        }

        public async Task Bootstrap(CancellationToken token)
        {
            await kademlia.Bootstrap(token);
        }

        public void AddOrRefresh(IEnr node)
        {
            kademlia.AddOrRefresh(node);
        }
    }

}
