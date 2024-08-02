// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Session;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal;
using Nethermind.Network.Test;
using NonBlocking;
using NUnit.Framework;
using Org.BouncyCastle.Utilities;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Network.Discovery.Test.Portal;

public class ContentNetworkScenarioTests
{
    [Test]
    public async Task TestSmallContentTransfer()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        Scenario scenario = new Scenario();

        var node1 = scenario.CreateNode();
        var key = scenario.GenerateRandomBytes(32);
        var value = scenario.GenerateRandomBytes(50);
        node1.SetStore(key, value);

        var node2 = scenario.CreateNode();
        node2.AddPeer(node1);

        var lookupResult = await node2.ContentNetwork.LookupContent(key, cts.Token);
        lookupResult.Should().BeEquivalentTo(value);
    }

    [Test]
    public async Task TestLargeContentTransfer()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        Scenario scenario = new Scenario();

        var node1 = scenario.CreateNode();
        var key = scenario.GenerateRandomBytes(32);
        var value = scenario.GenerateRandomBytes(50_000);
        node1.SetStore(key, value);

        var node2 = scenario.CreateNode();
        node2.AddPeer(node1);

        (await node2.ContentNetwork.LookupContent(key, cts.Token)).Should().BeEquivalentTo(value);
    }

    [Test]
    public async Task TestSmallContentTransferWithLookups()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        Scenario scenario = new Scenario();

        var node1 = scenario.CreateNode();
        var key = scenario.GenerateRandomBytes(32);
        var value = scenario.GenerateRandomBytes(50);
        node1.SetStore(key, value);

        var node2 = scenario.CreateNode();
        node2.AddPeer(node1);

        var node3 = scenario.CreateNode();
        node3.AddPeer(node2);

        var node4 = scenario.CreateNode();
        node4.AddPeer(node3);

        (await node4.ContentNetwork.LookupContent(key, cts.Token)).Should().BeEquivalentTo(value);
    }


    [Test]
    public async Task TestLargeContentTransferWithLookups()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(1));

        Scenario scenario = new Scenario();

        var node1 = scenario.CreateNode();
        var key = scenario.GenerateRandomBytes(32);
        var value = scenario.GenerateRandomBytes(50_000);
        node1.SetStore(key, value);

        var node2 = scenario.CreateNode();
        node2.AddPeer(node1);

        var node3 = scenario.CreateNode();
        node3.AddPeer(node2);

        var node4 = scenario.CreateNode();
        node4.AddPeer(node3);

        (await node4.ContentNetwork.LookupContent(key, cts.Token)).Should().BeEquivalentTo(value);
    }

    private class Scenario
    {
        private readonly byte[] ProtocolId = [0, 0];
        private readonly Random _rng = new Random(0);
        private readonly PrivateKeyGenerator _privateKeyGenerator;
        private readonly IdentityVerifierV4 _identityVerifier = new();
        private readonly EnrEntryRegistry _registry = new();
        private readonly EnrFactory _enrFactory;

        private readonly ConcurrentDictionary<ValueHash256, Node> _nodes = new();

        public Scenario()
        {
            _privateKeyGenerator = new PrivateKeyGenerator(new TestRandom((n) => _rng.Next(), GenerateRandomBytes));
            _enrFactory = new(_registry);
        }

        internal class Node(
            IEnr enr,
            IPortalContentNetwork contentNetwork,
            TestStore testStore,
            ServiceProvider serviceProvider)
        {
            internal IPortalContentNetwork ContentNetwork => contentNetwork;

            internal ITalkReqTransport TalkReqTransport => serviceProvider.GetRequiredService<ITalkReqTransport>();

            internal IEnr Enr => enr;

            public void SetStore(byte[] contentId, byte[] value)
            {
                testStore.Set(contentId, value);
            }

            public void AddPeer(Node otherNode)
            {
                contentNetwork.AddOrRefresh(otherNode.Enr);
            }
        }

        public Node CreateNode()
        {
            PrivateKey privateKey = _privateKeyGenerator.Generate();
            SessionOptions sessionOptions = new SessionOptions
            {
                Signer = new IdentitySignerV4(privateKey.KeyBytes),
                Verifier = _identityVerifier,
                SessionKeys = new SessionKeys(privateKey.KeyBytes),
            };

            IEnr newNodeEnr = new EnrBuilder()
                .WithIdentityScheme(sessionOptions.Verifier, sessionOptions.Signer)
                .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
                .WithEntry(EnrEntryKey.Secp256K1,
                    new EntrySecp256K1(NBitcoin.Secp256k1.Context.Instance.CreatePubKey(privateKey.PublicKey.PrefixedBytes)
                        .ToBytes(false)))
                .Build();

            IEnrProvider enrProvider = new TestEnrProvider(newNodeEnr, _identityVerifier, _enrFactory);
            IRawTalkReqSender talkReqSender = CreateTalkReqSenderFor(newNodeEnr);
            IServiceCollection services = new ServiceCollection()
                .AddSingleton<ILogManager>(new TestLogManager())
                .AddSingleton(enrProvider)
                .AddSingleton(talkReqSender);

            ComponentConfiguration.Configure(services);

            var serviceProvider = services.BuildServiceProvider();

            IPortalContentNetworkFactory factory = serviceProvider.GetRequiredService<IPortalContentNetworkFactory>();
            TestStore testStore = new TestStore();
            IPortalContentNetwork contentNetwork = factory.Create(ProtocolId, testStore);
            Node node = new Node(newNodeEnr, contentNetwork, testStore, serviceProvider);
            _nodes[EnrNodeHashProvider.Instance.GetHash(newNodeEnr)] = node;

            return node;
        }

        private IRawTalkReqSender CreateTalkReqSenderFor(IEnr newNodeEnr)
        {
            return new TestTalkReqSender(newNodeEnr, this);
        }

        private class TestTalkReqSender(IEnr sender, Scenario scenario): IRawTalkReqSender
        {
            public Task<TalkReqMessage> SentTalkReq(IEnr receiver, byte[] protocol, byte[] message, CancellationToken token)
            {
                if(scenario._nodes.TryGetValue(EnrNodeHashProvider.Instance.GetHash(receiver), out Node? receiverNode))
                {
                    var talkReq = new TalkReqMessage(protocol, message);

                    // So its a bit weird here. Because the sender need to know the message id in order to track thee talkresp,
                    // this need to return the talkreq first for the request id to get recorded, and then actually send the
                    // talkreq
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(10, token);
                            var response = await receiverNode.TalkReqTransport.OnTalkReq(sender, talkReq);
                            if (response != null && scenario._nodes.TryGetValue(EnrNodeHashProvider.Instance.GetHash(sender), out Node? senderNode))
                            {
                                var talkResp = new TalkRespMessage(protocol, response);
                                talkResp.RequestId = talkReq.RequestId;
                                senderNode.TalkReqTransport.OnTalkResp(receiver, talkResp);
                            }
                        }
                        catch (Exception e)
                        {
                            await TestContext.Error.WriteLineAsync($"Error responding to talkreq {e}");
                        }
                    }, token);

                    return Task.FromResult(talkReq);
                }

                throw new Exception($"unknown node {receiver}");
            }
        }

        internal class TestStore() : IPortalContentNetwork.Store
        {
            private SpanConcurrentDictionary<byte, byte[]> _contents = new(Bytes.SpanEqualityComparer);

            public byte[]? GetContent(byte[] contentKey)
            {
                return _contents.GetValueOrDefault(contentKey);
            }

            public bool ShouldAcceptOffer(byte[] offerContentKey)
            {
                return false;
            }

            public void Store(byte[] contentKey, byte[] content)
            {
                _contents[contentKey] = content;
            }

            public void Set(byte[] contentId, byte[] value)
            {
                _contents[contentId] = value;
            }
        }

        private class TestEnrProvider(
            IEnr self,
            IIdentityVerifier identityVerifier,
            IEnrFactory enrFactory
        ): IEnrProvider
        {
            public IEnr Decode(byte[] enrBytes)
            {
                return enrFactory.CreateFromBytes(enrBytes, identityVerifier);
            }

            public IEnr SelfEnr => self;
        }

        public byte[] GenerateRandomBytes(int size)
        {
            byte[] bytes = new byte[size];
            _rng.NextBytes(bytes);
            return bytes;
        }
    }
}
