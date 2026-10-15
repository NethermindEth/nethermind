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
        Assert.That(banner, Does.StartWith(Environment.NewLine +
            "     _____   ____  ___  __    ______"));
        Assert.That(banner, Does.Not.Contain("\u001b"));
        Assert.That(banner, Does.Contain(Environment.NewLine + Environment.NewLine +
            "MAINNET | Beacon + execution P2P | Local verification"));
        Assert.That(banner, Does.Contain(Environment.NewLine +
            "RPC http://127.0.0.1:8545 | P2P :9050 / :30307"));
        Assert.That(banner, Does.Contain(checkpoint));
        Assert.That(banner, Does.EndWith("--------------------------------------------------------------------" +
            Environment.NewLine + Environment.NewLine));
    }

    [Test]
    public void Interactive_banner_uses_blue_and_orange_logo()
    {
        string banner = LightClientBanner.Render("mainnet", "http://127.0.0.1:8545", "0x01", redirected: false);

        Assert.That(banner, Does.Contain("     \u001b[1;38;2;0;179;255m_____   ____  ___" +
            "\u001b[1;38;2;255;153;0m  __    ______\u001b[0m"));
        Assert.That(banner, Does.Contain("     \u001b[1;38;2;0;179;255m/_/ |_/_/  /_/" +
            "\u001b[1;38;2;255;153;0m /_____/\\____/\u001b[0m"));
    }
}
