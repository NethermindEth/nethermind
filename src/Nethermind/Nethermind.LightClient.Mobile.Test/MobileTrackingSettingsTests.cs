// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.LightClient.Mobile;

namespace Nethermind.LightClient.Test;

public class MobileTrackingSettingsTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("0x0")]
    [TestCase("0xzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [TestCase("C02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2")]
    [TestCase("0xc02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2")]
    public void Invalid_or_mistyped_accounts_are_rejected_without_throwing(string? input) =>
        Assert.That(MobileTrackingSettings.TryCreate(input, true, true, true, out _), Is.False);

    [Test]
    public void Address_is_normalized_and_selection_is_exact([Values] bool usdt, [Values] bool dai, [Values] bool usdc)
    {
        Assert.That(MobileTrackingSettings.TryCreate(" 0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2 ", usdt, dai, usdc, out MobileTrackingSettings? settings), Is.True);
        MobileTrackingSettings expected = new("0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2", usdt, dai, usdc);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings, Is.EqualTo(expected));
            Assert.That(settings!.SelectedTokens().Select(token => token.Symbol), Is.EqualTo(
                new[] { usdt ? "USDT" : null, dai ? "DAI" : null, usdc ? "USDC" : null }.Where(symbol => symbol is not null)));
        }
    }

    [Test]
    public void Native_eth_can_be_tracked_without_tokens()
    {
        Assert.That(MobileTrackingSettings.TryCreate("0x0000000000000000000000000000000000000000", false, false, false, out MobileTrackingSettings? settings), Is.True);
        MobileProgress progress = new("Following mainnet", HeadFresh: true, BalanceAtHead: true,
            Balance: new("0", 1, "hash", "root", DateTimeOffset.UtcNow, 1), Tokens: [], Settings: settings);
        Assert.That(progress.IsSynced, Is.True);
    }
}
