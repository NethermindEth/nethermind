// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PeerRandomizerServiceTests
    {
        [Test]
        public void When_disabled_returns_zero_score()
        {
            IDb metadataDb = new MemDb();
            PeerRandomizerService service = new(metadataDb, isEnabled: false, LimboLogs.Instance);

            long score = service.GetRandomizedScore(TestItem.PublicKeyA);

            Assert.That(service.IsEnabled, Is.False);
            Assert.That(score, Is.EqualTo(0));
        }

        [Test]
        public void When_enabled_returns_non_zero_score()
        {
            IDb metadataDb = new MemDb();
            PeerRandomizerService service = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            long score = service.GetRandomizedScore(TestItem.PublicKeyA);

            Assert.That(service.IsEnabled, Is.True);
            Assert.That(score, Is.GreaterThan(0));
        }

        [Test]
        public void Same_peer_gets_same_score()
        {
            IDb metadataDb = new MemDb();
            PeerRandomizerService service = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            long score1 = service.GetRandomizedScore(TestItem.PublicKeyA);
            long score2 = service.GetRandomizedScore(TestItem.PublicKeyA);

            Assert.That(score1, Is.EqualTo(score2));
        }

        [Test]
        public void Different_peers_get_different_scores()
        {
            IDb metadataDb = new MemDb();
            PeerRandomizerService service = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            long scoreA = service.GetRandomizedScore(TestItem.PublicKeyA);
            long scoreB = service.GetRandomizedScore(TestItem.PublicKeyB);
            long scoreC = service.GetRandomizedScore(TestItem.PublicKeyC);

            Assert.That(scoreA, Is.Not.EqualTo(scoreB));
            Assert.That(scoreB, Is.Not.EqualTo(scoreC));
            Assert.That(scoreA, Is.Not.EqualTo(scoreC));
        }

        [Test]
        public void Randomized_seed_is_persisted()
        {
            IDb metadataDb = new MemDb();
            PeerRandomizerService service1 = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            long score1 = service1.GetRandomizedScore(TestItem.PublicKeyA);

            PeerRandomizerService service2 = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            long score2 = service2.GetRandomizedScore(TestItem.PublicKeyA);

            Assert.That(score1, Is.EqualTo(score2));
        }

        [Test]
        public void Randomized_scores_are_positive()
        {
            IDb metadataDb = new MemDb();
            PeerRandomizerService service = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            for (int i = 0; i < 100; i++)
            {
                long score = service.GetRandomizedScore(TestItem.PublicKeys[i % TestItem.PublicKeys.Length]);
                Assert.That(score, Is.GreaterThanOrEqualTo(0));
            }
        }
    }
}
