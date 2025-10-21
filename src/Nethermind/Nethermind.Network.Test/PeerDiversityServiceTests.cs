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
    public class PeerDiversityServiceTests
    {
        [Test]
        public void When_disabled_returns_zero_score()
        {
            IDb metadataDb = new MemDb();
            PeerDiversityService service = new(metadataDb, isEnabled: false, LimboLogs.Instance);

            long score = service.GetDiversityScore(TestItem.PublicKeyA);

            Assert.That(service.IsEnabled, Is.False);
            Assert.That(score, Is.EqualTo(0));
        }

        [Test]
        public void When_enabled_returns_non_zero_score()
        {
            IDb metadataDb = new MemDb();
            PeerDiversityService service = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            long score = service.GetDiversityScore(TestItem.PublicKeyA);

            Assert.That(service.IsEnabled, Is.True);
            Assert.That(score, Is.GreaterThan(0));
        }

        [Test]
        public void Same_peer_gets_same_score()
        {
            IDb metadataDb = new MemDb();
            PeerDiversityService service = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            long score1 = service.GetDiversityScore(TestItem.PublicKeyA);
            long score2 = service.GetDiversityScore(TestItem.PublicKeyA);

            Assert.That(score1, Is.EqualTo(score2));
        }

        [Test]
        public void Different_peers_get_different_scores()
        {
            IDb metadataDb = new MemDb();
            PeerDiversityService service = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            long scoreA = service.GetDiversityScore(TestItem.PublicKeyA);
            long scoreB = service.GetDiversityScore(TestItem.PublicKeyB);
            long scoreC = service.GetDiversityScore(TestItem.PublicKeyC);

            Assert.That(scoreA, Is.Not.EqualTo(scoreB));
            Assert.That(scoreB, Is.Not.EqualTo(scoreC));
            Assert.That(scoreA, Is.Not.EqualTo(scoreC));
        }

        [Test]
        public void Diversity_seed_is_persisted()
        {
            IDb metadataDb = new MemDb();
            PeerDiversityService service1 = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            long score1 = service1.GetDiversityScore(TestItem.PublicKeyA);

            PeerDiversityService service2 = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            long score2 = service2.GetDiversityScore(TestItem.PublicKeyA);

            Assert.That(score1, Is.EqualTo(score2));
        }

        [Test]
        public void Diversity_scores_are_positive()
        {
            IDb metadataDb = new MemDb();
            PeerDiversityService service = new(metadataDb, isEnabled: true, LimboLogs.Instance);

            for (int i = 0; i < 100; i++)
            {
                long score = service.GetDiversityScore(TestItem.PublicKeys[i % TestItem.PublicKeys.Length]);
                Assert.That(score, Is.GreaterThanOrEqualTo(0));
            }
        }
    }
}
