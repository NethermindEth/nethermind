// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture]
    public class TxFloodControllerTests
    {
        private TxFloodController _controller;
        private Eth62ProtocolHandler _handler;
        private ISession _session;
        private ITimestamper _timestamper;
        private DateTime _now;

        private readonly AcceptTxResult Flooding = AcceptTxResult.NonceGap;

        [SetUp]
        public void Setup()
        {
            _session = Substitute.For<ISession>();
            _handler = new Eth62ProtocolHandler(
                _session,
                Substitute.For<IMessageSerializationService>(),
                Substitute.For<INodeStatsManager>(),
                Substitute.For<ISyncServer>(),
                RunImmediatelyScheduler.Instance,
                Substitute.For<ITxPool>(),
                Substitute.For<IGossipPolicy>(),
                LimboLogs.Instance);

            _timestamper = Substitute.For<ITimestamper>();
            _now = DateTime.UtcNow;
            _timestamper.UtcNow.Returns(_ => _now);
            _controller = new TxFloodController(_handler, _timestamper, LimboNoErrorLogger.Instance, new Random(0));
        }

        [TearDown]
        public void TearDown()
        {
            _handler?.Dispose();
            _session?.Dispose();
        }

        [Test]
        public void Is_allowed_will_be_true_unless_misbehaving()
        {
            for (int i = 0; i < 10000; i++)
            {
                Assert.That(_controller.IsAllowed(), Is.True);
            }
        }

        [Test]
        public void Is_allowed_will_be_false_when_misbehaving()
        {
            for (int i = 0; i < 601; i++)
            {
                _controller.Report(Flooding);
            }

            int allowedCount = 0;
            for (int i = 0; i < 10000; i++)
            {
                if (_controller.IsAllowed()) allowedCount++;
            }

            Assert.That(allowedCount, Is.InRange(500, 1500));
        }

        [Test]
        public void Will_only_get_disconnected_when_really_flooding()
        {
            for (int i = 0; i < 600; i++)
            {
                _controller.Report(Flooding);
            }

            // for easier debugging
            _controller.Report(Flooding);

            _session.DidNotReceiveWithAnyArgs()
                .InitiateDisconnect(DisconnectReason.TxFlooding, null);

            for (int i = 0; i < 6000 - 601; i++)
            {
                _controller.Report(Flooding);
            }

            // for easier debugging
            _controller.Report(Flooding);

            _session.Received()
                .InitiateDisconnect(DisconnectReason.TxFlooding, Arg.Any<string>());
        }

        [Test]
        public void Will_downgrade_at_first()
        {
            for (int i = 0; i < 1000; i++)
            {
                _controller.Report(Flooding);
            }

            Assert.That(_controller.IsDowngraded, Is.True);
        }

        [Test]
        public void Clearing_pooled_requests_preserves_transaction_flood_downgrade()
        {
            for (int i = 0; i < 1000; i++)
            {
                _controller.Report(Flooding);
            }

            _controller.ClearPooledTransactionRequests();

            Assert.That(_controller.IsDowngraded, Is.True);
        }

        [Test]
        public void Enabled_by_default() => Assert.That(_controller.IsEnabled, Is.True);

        [Test]
        public void Can_be_disabled_and_enabled()
        {
            _controller.IsEnabled = false;
            Assert.That(_controller.IsEnabled, Is.False);
            _controller.IsEnabled = false;
            Assert.That(_controller.IsEnabled, Is.False);
            _controller.IsEnabled = true;
            Assert.That(_controller.IsEnabled, Is.True);
            _controller.IsEnabled = true;
            Assert.That(_controller.IsEnabled, Is.True);
        }

        [Test]
        public void Misbehaving_expires()
        {
            for (int i = 0; i < 1000; i++)
            {
                _controller.Report(Flooding);
            }

            Assert.That(_controller.IsDowngraded, Is.True);
            AdvanceWindow();
            _controller.Report(false);
            Assert.That(_controller.IsDowngraded, Is.False);
        }

        [TestCase(4_095, 0, false)]
        [TestCase(4_096, 327, true)]
        [TestCase(4_096, 328, false)]
        public void Pooled_hash_gate_requires_large_unproductive_sample(int requested, int returned, bool downgraded)
        {
            ReportPooledHashWindow(requested, returned);
            ClosePooledHashWindow();

            Assert.That(_controller.IsDowngraded, Is.EqualTo(downgraded));
        }

        [Test]
        public void Repeated_unproductive_pooled_hash_windows_disconnect()
        {
            ReportPooledHashWindow(4_096, 0);

            _session.DidNotReceiveWithAnyArgs().InitiateDisconnect(DisconnectReason.TxFlooding, null);

            ReportPooledHashWindow(4_096, 0);

            _session.DidNotReceiveWithAnyArgs().InitiateDisconnect(DisconnectReason.TxFlooding, null);

            ClosePooledHashWindow();

            _session.Received(1).InitiateDisconnect(
                DisconnectReason.TxFlooding,
                Arg.Is<string>(details => details.Contains("returned 0 of 4096 sampled")));
        }

        [Test]
        public void Productive_pooled_hash_window_clears_disconnect_strike()
        {
            ReportPooledHashWindow(4_096, 0);
            ClosePooledHashWindow();
            ReportPooledHashWindow(4_096, 410, 4_096);
            ClosePooledHashWindow();
            ReportPooledHashWindow(4_096, 0, 8_192);
            ClosePooledHashWindow();

            _session.DidNotReceiveWithAnyArgs().InitiateDisconnect(DisconnectReason.TxFlooding, null);
            Assert.That(_controller.IsDowngraded, Is.True);
        }

        [Test]
        public void Unrequested_and_duplicate_pooled_transactions_do_not_receive_credit()
        {
            Hash256[] requested = GenerateHashes(4_096);
            Hash256[] unrequested = GenerateHashes(409, requested.Length);
            Transaction[] transactions = new Transaction[410];
            transactions[0] = new Transaction { Hash = requested[0] };
            for (int i = 1; i < transactions.Length; i++)
            {
                transactions[i] = new Transaction { Hash = i % 2 == 0 ? requested[0] : unrequested[i - 1] };
            }

            _controller.ReportPooledTransactionRequests(requested);
            _controller.ReportPooledTransactionsReturned(transactions);
            ClosePooledHashWindow();
            ClosePooledHashWindow();

            Assert.That(_controller.IsDowngraded, Is.True);
        }

        [Test]
        public void Pooled_transactions_returned_in_grace_window_receive_credit()
        {
            Hash256[] requested = GenerateHashes(4_096);

            _controller.ReportPooledTransactionRequests(requested);
            ClosePooledHashWindow();
            _controller.ReportPooledTransactionsReturned(CreateTransactions(requested, 410));
            ClosePooledHashWindow();

            _session.DidNotReceiveWithAnyArgs().InitiateDisconnect(DisconnectReason.TxFlooding, null);
            Assert.That(_controller.IsDowngraded, Is.False);
        }

        [Test]
        public void Pooled_hash_sample_is_not_limited_to_first_requests()
        {
            Hash256[] requested = GenerateHashes(8_192);

            _controller.ReportPooledTransactionRequests(requested);
            _controller.ReportPooledTransactionsReturned(CreateTransactions(requested, 410));
            ClosePooledHashWindow();
            ClosePooledHashWindow();

            Assert.That(_controller.IsDowngraded, Is.True);
        }

        [Test]
        public void Unevaluated_pooled_hash_window_preserves_disconnect_strike()
        {
            ReportPooledHashWindow(4_096, 0);
            ClosePooledHashWindow();
            ReportPooledHashWindow(4_095, 0);
            ClosePooledHashWindow();

            Assert.That(_controller.IsDowngraded, Is.True);

            ReportPooledHashWindow(4_096, 0);

            _session.DidNotReceiveWithAnyArgs().InitiateDisconnect(DisconnectReason.TxFlooding, null);

            ClosePooledHashWindow();

            _session.Received(1).InitiateDisconnect(DisconnectReason.TxFlooding, Arg.Any<string>());
        }

        [Test]
        public void Response_for_hash_requested_across_window_boundary_credits_both_samples()
        {
            Hash256[] requested = GenerateHashes(4_096);

            _controller.ReportPooledTransactionRequests(requested);
            ClosePooledHashWindow();
            _controller.ReportPooledTransactionRequests(requested);
            _controller.ReportPooledTransactionsReturned(CreateTransactions(requested, 410));
            ClosePooledHashWindow();

            Assert.That(_controller.IsDowngraded, Is.False);
        }

        [Test]
        public void Ignored_pooled_response_clears_outstanding_samples()
        {
            _controller.ReportPooledTransactionRequests(GenerateHashes(4_096));
            ClosePooledHashWindow();

            _controller.ClearPooledTransactionRequests();
            ClosePooledHashWindow();

            Assert.That(_controller.IsDowngraded, Is.False);
        }

        [Test]
        public void Clearing_pooled_requests_preserves_evaluated_pooled_downgrade()
        {
            ReportPooledHashWindow(4_096, 0);
            ClosePooledHashWindow();

            _controller.ClearPooledTransactionRequests();

            Assert.That(_controller.IsDowngraded, Is.True);
        }

        [Test]
        public void Will_disconnect_on_invalid_tx()
        {
            _controller.Report(AcceptTxResult.Invalid);

            _session.Received(1)
                .InitiateDisconnect(DisconnectReason.InvalidTxReceived, "invalid tx");
        }

        private void ReportPooledHashWindow(int requested, int returned, int offset = 0)
        {
            Hash256[] hashes = GenerateHashes(requested, offset);

            _controller.ReportPooledTransactionRequests(hashes);
            _controller.ReportPooledTransactionsReturned(CreateTransactions(hashes, returned));
            ClosePooledHashWindow();
        }

        private static Transaction[] CreateTransactions(Hash256[] hashes, int count)
        {
            Transaction[] transactions = new Transaction[count];
            for (int i = 0; i < transactions.Length; i++)
            {
                transactions[i] = new Transaction { Hash = hashes[i] };
            }

            return transactions;
        }

        private void ClosePooledHashWindow()
        {
            AdvanceWindow();
            _controller.IsAllowed();
        }

        private static Hash256[] GenerateHashes(int count, int offset = 0)
        {
            Hash256[] hashes = new Hash256[count];
            for (int i = 0; i < hashes.Length; i++)
            {
                hashes[i] = new Hash256((i + offset).ToString("X64"));
            }

            return hashes;
        }

        private void AdvanceWindow() => _now += TimeSpan.FromSeconds(61);
    }
}
