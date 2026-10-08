// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.LightClient.Test;

public class LightClientBannerTests
{
    [Test]
    public void Redirected_banner_uses_ascii_and_includes_connection_details()
    {
        const string checkpoint = "0x6ac776d12a0f63834f8ebac4882ecc4ee863b2f0f349a765ab2058c92eb18047";
        string banner = LightClientBanner.Render("mainnet", "http://127.0.0.1:8545", checkpoint, redirected: true);

        Assert.That(banner.All(char.IsAscii), Is.True);
        Assert.That(banner, Does.Contain("N E T H E R M I N D"));
        Assert.That(banner, Does.Contain("MAINNET | Beacon + execution P2P | Local verification"));
        Assert.That(banner, Does.Contain("RPC http://127.0.0.1:8545 | P2P :9050 / :30307"));
        Assert.That(banner, Does.Contain(checkpoint));
    }
}
