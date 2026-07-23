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
        [TestCase(4_096, 0, false)]
        [TestCase(32_768, 1, true)]
        [TestCase(32_768, 2, false)]
        public void Pooled_request_gate_requires_large_unproductive_sample(
            int requested,
            int usefulResponses,
            bool downgraded)
        {
            ReportPooledRequestWindow(requested, usefulResponses);
            ClosePooledRequestWindow();

            Assert.That(_controller.IsDowngraded, Is.EqualTo(downgraded));
        }

        [Test]
        public void Repeated_unproductive_pooled_request_windows_disconnect()
        {
            ReportPooledRequestWindow(32_768, 0);

            _session.DidNotReceiveWithAnyArgs().InitiateDisconnect(DisconnectReason.TxFlooding, null);

            ReportPooledRequestWindow(32_768, 0);

            _session.DidNotReceiveWithAnyArgs().InitiateDisconnect(DisconnectReason.TxFlooding, null);

            ClosePooledRequestWindow();

            _session.Received(1).InitiateDisconnect(
                DisconnectReason.TxFlooding,
                Arg.Is<string>(details => details.Contains("returned requested transactions in 0 of 128 sampled request messages")));
        }

        [Test]
        public void Productive_pooled_request_window_clears_disconnect_strike()
        {
            ReportPooledRequestWindow(32_768, 0);
            ClosePooledRequestWindow();
            ReportPooledRequestWindow(32_768, 2, 32_768);
            ClosePooledRequestWindow();
            ReportPooledRequestWindow(32_768, 0, 65_536);
            ClosePooledRequestWindow();

            _session.DidNotReceiveWithAnyArgs().InitiateDisconnect(DisconnectReason.TxFlooding, null);
            Assert.That(_controller.IsDowngraded, Is.True);
        }

        [Test]
        public void Unrequested_and_duplicate_pooled_transactions_do_not_receive_credit()
        {
            Hash256[] requested = GenerateHashes(32_768);
            Hash256[] unrequested = GenerateHashes(2, requested.Length);

            ReportPooledRequests(requested);
            _controller.ReportPooledTransactionsReturned(
                [new Transaction { Hash = requested[0] }, new Transaction { Hash = unrequested[0] }]);
            _controller.ReportPooledTransactionsReturned(
                [new Transaction { Hash = requested[0] }, new Transaction { Hash = unrequested[1] }]);
            ClosePooledRequestWindow();
            ClosePooledRequestWindow();

            Assert.That(_controller.IsDowngraded, Is.True);
        }

        [Test]
        public void Pooled_transactions_returned_in_grace_window_receive_credit()
        {
            Hash256[] requested = GenerateHashes(32_768);

            ReportPooledRequests(requested);
            ClosePooledRequestWindow();
            ReportUsefulResponses(requested, 2);
            ClosePooledRequestWindow();

            _session.DidNotReceiveWithAnyArgs().InitiateDisconnect(DisconnectReason.TxFlooding, null);
            Assert.That(_controller.IsDowngraded, Is.False);
        }

        [Test]
        public void Pooled_request_sample_is_not_limited_to_first_requests()
        {
            Hash256[] requested = GenerateHashes(65_536);

            ReportPooledRequests(requested);
            ReportUsefulResponses(requested, 128, requestOffset: 128);
            ClosePooledRequestWindow();
            ClosePooledRequestWindow();

            Assert.That(_controller.IsDowngraded, Is.False);
        }

        [Test]
        public void One_returned_transaction_per_request_page_is_productive()
        {
            Hash256[] requested = GenerateHashes(32_768);

            ReportPooledRequests(requested);
            for (int i = 0; i < requested.Length; i += 256)
            {
                _controller.ReportPooledTransactionsReturned([new Transaction { Hash = requested[i] }]);
            }

            ClosePooledRequestWindow();
            ClosePooledRequestWindow();

            Assert.That(_controller.IsDowngraded, Is.False);
        }

        [Test]
        public void Swapped_hash_halves_do_not_receive_credit()
        {
            byte[] requestedBytes = new byte[Hash256.Size];
            for (int i = 0; i < requestedBytes.Length; i++)
            {
                requestedBytes[i] = (byte)i;
            }

            byte[] returnedBytes = [.. requestedBytes[16..], .. requestedBytes[..16]];
            Hash256[] requested = GenerateHashes(32_768);
            requested[0] = new Hash256(requestedBytes);

            ReportPooledRequests(requested);
            ReportUsefulResponses(requested, 1, requestOffset: 1);
            _controller.ReportPooledTransactionsReturned(
                [new Transaction { Hash = new Hash256(returnedBytes) }]);
            ClosePooledRequestWindow();
            ClosePooledRequestWindow();

            Assert.That(_controller.IsDowngraded, Is.True);
        }

        [Test]
        public void Unevaluated_pooled_request_window_preserves_disconnect_strike()
        {
            ReportPooledRequestWindow(32_768, 0);
            ClosePooledRequestWindow();
            ReportPooledRequestWindow(4_096, 0);
            ClosePooledRequestWindow();

            Assert.That(_controller.IsDowngraded, Is.True);

            ReportPooledRequestWindow(32_768, 0);

            _session.DidNotReceiveWithAnyArgs().InitiateDisconnect(DisconnectReason.TxFlooding, null);

            ClosePooledRequestWindow();

            _session.Received(1).InitiateDisconnect(DisconnectReason.TxFlooding, Arg.Any<string>());
        }

        [Test]
        public void Response_for_hash_requested_across_window_boundary_credits_only_oldest_sample()
        {
            Hash256[] requested = GenerateHashes(32_768);

            ReportPooledRequests(requested);
            ClosePooledRequestWindow();
            ReportPooledRequests(requested);
            ReportUsefulResponses(requested, 2);
            ReportUsefulResponses(requested, 2);
            ClosePooledRequestWindow();
            ClosePooledRequestWindow();

            Assert.That(_controller.IsDowngraded, Is.True);
        }

        [Test]
        public void Partial_response_does_not_suppress_retry_credit_for_other_hashes()
        {
            Hash256[] requested = GenerateHashes(32_768);

            ReportPooledRequests(requested);
            ClosePooledRequestWindow();
            ReportPooledRequests(requested);
            _controller.ReportPooledTransactionsReturned(
                [new Transaction { Hash = requested[0] }]);
            _controller.ReportPooledTransactionsReturned(
                [new Transaction { Hash = requested[256] }]);
            _controller.ReportPooledTransactionsReturned(
                [new Transaction { Hash = requested[1] }]);
            _controller.ReportPooledTransactionsReturned(
                [new Transaction { Hash = requested[257] }]);
            ClosePooledRequestWindow();
            ClosePooledRequestWindow();

            Assert.That(_controller.IsDowngraded, Is.False);
        }

        [Test]
        public void Ignored_pooled_response_clears_outstanding_samples()
        {
            ReportPooledRequests(GenerateHashes(4_096));
            ClosePooledRequestWindow();

            _controller.ClearPooledTransactionRequests();
            ClosePooledRequestWindow();

            Assert.That(_controller.IsDowngraded, Is.False);
        }

        [Test]
        public void Clearing_pooled_requests_preserves_evaluated_pooled_downgrade()
        {
            ReportPooledRequestWindow(32_768, 0);
            ClosePooledRequestWindow();

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

        private void ReportPooledRequestWindow(int requested, int usefulResponses, int offset = 0)
        {
            Hash256[] hashes = GenerateHashes(Math.Min(requested, 32_768), offset);

            ReportRepeatedPooledRequests(hashes, requested);
            ReportUsefulResponses(hashes, usefulResponses);
            ClosePooledRequestWindow();
        }

        private void ReportRepeatedPooledRequests(Hash256[] hashes, int requested)
        {
            while (requested > 0)
            {
                int count = Math.Min(hashes.Length, requested);
                ReportPooledRequests(hashes.AsSpan(0, count));
                requested -= count;
            }
        }

        private void ReportPooledRequests(Hash256[] hashes) => ReportPooledRequests(hashes.AsSpan());

        private void ReportPooledRequests(ReadOnlySpan<Hash256> hashes)
        {
            const int maxHashesPerRequest = 256;
            for (int start = 0; start < hashes.Length; start += maxHashesPerRequest)
            {
                int count = Math.Min(maxHashesPerRequest, hashes.Length - start);
                _controller.ReportPooledTransactionRequest(hashes.Slice(start, count));
            }
        }

        private void ReportUsefulResponses(Hash256[] hashes, int count, int requestOffset = 0)
        {
            for (int i = 0; i < count; i++)
            {
                int hashIndex = (requestOffset + i) * 256;
                _controller.ReportPooledTransactionsReturned(
                    [new Transaction { Hash = hashes[hashIndex] }]);
            }
        }

        private void ClosePooledRequestWindow()
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
