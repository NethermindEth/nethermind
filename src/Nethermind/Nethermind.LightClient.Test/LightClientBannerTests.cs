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
        Assert.That(banner, Does.StartWith("_____   __   ______  ___   ______     _________"));
        Assert.That(banner, Does.Not.Contain("\u001b"));
        Assert.That(banner, Does.Contain("MAINNET | Beacon + execution P2P | Local verification"));
        Assert.That(banner, Does.Contain("RPC http://127.0.0.1:8545 | P2P :9050 / :30307"));
        Assert.That(banner, Does.Contain(checkpoint));
    }

    [Test]
    public void Interactive_banner_uses_blue_and_orange_logo()
    {
        string banner = LightClientBanner.Render("mainnet", "http://127.0.0.1:8545", "0x01", redirected: false);

        Assert.That(banner, Does.StartWith("\u001b[1;38;2;0;179;255m_____   __   ______  ___   " +
            "\u001b[1;38;2;255;153;0m______     _________"));
        Assert.That(banner, Does.Contain("/_____/    \\____/\u001b[0m"));
    }
}
