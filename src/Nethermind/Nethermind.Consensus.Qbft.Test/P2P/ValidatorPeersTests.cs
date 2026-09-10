// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.P2P;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.P2P;

[Parallelizable(ParallelScope.All)]
public class ValidatorPeersTests
{
    private sealed class StubPeer(Address address) : IQbftPeer
    {
        public Address NodeAddress { get; } = address;
        public int Sent { get; private set; }
        public void Send(int code, byte[] data) => Sent++;
    }

    private static ValidatorPeers CreatePeers(params Address[] validators)
    {
        IValidatorProvider provider = Substitute.For<IValidatorProvider>();
        provider.GetValidatorsAtHead().Returns(validators);
        return new ValidatorPeers(provider, LimboLogs.Instance);
    }

    [Test]
    public void ChurningPeersDoesNotGrowTheRegistry()
    {
        ValidatorPeers peers = CreatePeers();
        for (ulong i = 0; i < 1000; i++)
        {
            StubPeer peer = new(QbftTestData.Addr(i));
            peers.Add(peer);
            peers.Remove(peer);
        }

        Assert.That(peers.TrackedAddressCount, Is.EqualTo(0));
    }

    [Test]
    public void SecondConnectionFromTheSameAddressSurvivesTheFirstDisconnect()
    {
        ValidatorPeers peers = CreatePeers(QbftTestData.Addr(1));
        StubPeer first = new(QbftTestData.Addr(1));
        StubPeer second = new(QbftTestData.Addr(1));
        peers.Add(first);
        peers.Add(second);
        peers.Remove(first);

        peers.Send(QbftMessageCode.Prepare, [1]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers.ConnectedValidatorCount, Is.EqualTo(1));
            Assert.That(second.Sent, Is.EqualTo(1));
            Assert.That(first.Sent, Is.EqualTo(0));
        }
    }

    [Test]
    public void SendsOnlyToConnectedValidatorsAndSkipsTheDenylist()
    {
        ValidatorPeers peers = CreatePeers(QbftTestData.Addr(1), QbftTestData.Addr(2), QbftTestData.Addr(3));
        StubPeer validator = new(QbftTestData.Addr(1));
        StubPeer denied = new(QbftTestData.Addr(2));
        StubPeer nonValidator = new(QbftTestData.Addr(9));
        peers.Add(validator);
        peers.Add(denied);
        peers.Add(nonValidator);

        peers.Send(QbftMessageCode.Commit, [1], [QbftTestData.Addr(2)]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(validator.Sent, Is.EqualTo(1));
            Assert.That(denied.Sent, Is.EqualTo(0));
            Assert.That(nonValidator.Sent, Is.EqualTo(0));
            Assert.That(peers.ConnectedValidatorCount, Is.EqualTo(2));
        }
    }
}
