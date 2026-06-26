// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Torrent.Maui.Tests;

[TestFixture]
public sealed class TorrentUiSettingsTests
{
    [Test]
    public void ToClientOptions_maps_timeout_settings()
    {
        TorrentUiSettings settings = new()
        {
            TrackerTimeoutSeconds = 11,
            DhtLookupIntervalSeconds = 22,
            DhtLookupTimeoutSeconds = 33,
            PeerTimeoutSeconds = 44,
        };

        TorrentClientOptions options = settings.ToClientOptions("payload.torrent", "downloads");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.TrackerTimeout, Is.EqualTo(TimeSpan.FromSeconds(11)));
            Assert.That(options.DhtLookupInterval, Is.EqualTo(TimeSpan.FromSeconds(22)));
            Assert.That(options.DhtLookupTimeout, Is.EqualTo(TimeSpan.FromSeconds(33)));
            Assert.That(options.PeerTimeout, Is.EqualTo(TimeSpan.FromSeconds(44)));
        }
    }
}
