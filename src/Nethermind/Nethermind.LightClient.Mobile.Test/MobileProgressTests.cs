// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.LightClient.Mobile;

namespace Nethermind.LightClient.Test;

public class MobileProgressTests
{
    [TestCase("ETH", "0", 18, "0.00")]
    [TestCase("ETH", "1", 18, "0.00")]
    [TestCase("ETH", "4999999999999999", 18, "0.00")]
    [TestCase("ETH", "5000000000000000", 18, "0.01")]
    [TestCase("ETH", "995000000000000000", 18, "1.00")]
    [TestCase("ETH", "1000000000000000000", 18, "1.00")]
    [TestCase("ETH", "2109734496267142584245273", 18, "2,109,734.50")]
    [TestCase("USDC", "4999", 6, "0.00")]
    [TestCase("USDC", "5000", 6, "0.01")]
    [TestCase("USDC", "995000", 6, "1.00")]
    [TestCase("USDC", "1234567890", 6, "1,234.57")]
    [TestCase("DAI", "1005000000000000000", 18, "1.01")]
    public void Amounts_round_to_two_decimals_without_a_ticker_or_changing_verified_units(string symbol, string units, int decimals, string expected)
    {
        MobileTokenBalance proof = new(symbol, decimals, units, 42, "hash", "root", DateTimeOffset.UnixEpoch, 10);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof.AmountText, Is.EqualTo(expected));
            Assert.That(proof.Units, Is.EqualTo(units));
        }
    }

    [Test]
    public void Synced_requires_a_fresh_matching_balance_without_errors([Values] bool fresh, [Values] bool matches,
        [Values] bool hasProof, [Values] bool hasError)
    {
        MobileBalance? proof = hasProof ? new("1", 42, "hash", "root", DateTimeOffset.UnixEpoch, 10) : null;
        MobileProgress progress = new("Following mainnet", HeadFresh: fresh, BalanceAtHead: matches,
            Balance: proof, Error: hasError ? "Proof unavailable" : null);
        bool ready = fresh && matches && hasProof && !hasError;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.IsSynced, Is.EqualTo(ready));
            Assert.That(progress.StatusText, Is.EqualTo(ready ? "Synced" : "Syncing"));
            Assert.That(progress.IsCurrent(new("USDC", 6, "42", 42, "hash", "root")), Is.EqualTo(ready));
        }
    }

    [Test]
    public void Startup_and_stopped_phases_remain_visible([Values("Selecting checkpoint", "Stopping", "Stopped", "Stopped after error")] string stage) =>
        Assert.That(new MobileProgress(stage).StatusText, Is.EqualTo(stage));

    [Test]
    public void An_unverified_token_cannot_display_a_zero_balance([Values] bool error) =>
        Assert.That(new MobileTokenBalance("USDC", 6, Error: error ? "No peers" : null).AmountText,
            Is.EqualTo(error ? "Unavailable" : "Waiting for verified state"));

    [TestCase(null, null, "")]
    [TestCase(null, "No peers", "Refresh unavailable: No peers")]
    [TestCase("0", null, "Verified at block 42")]
    [TestCase("123", "No peers", "Verified at block 42\nRefresh unavailable: No peers")]
    public void Token_details_retain_the_proof_block_when_a_refresh_fails(string? units, string? error, string expected) =>
        Assert.That(new MobileTokenBalance("USDC", 6, units, 42, Error: error).DetailsText, Is.EqualTo(expected));

    [Test]
    public void Synced_requires_all_selected_tokens_at_the_same_head(
        [Values("ready", "missing", "pending", "error", "previous-head", "wrong-root", "wrong-number")] string state)
    {
        MobileTokenBalance token = new("USDC", 6, state == "pending" ? null : "42", state == "wrong-number" ? 41UL : 42UL,
            state == "previous-head" ? "old" : "hash", state == "wrong-root" ? "old" : "root", Error: state == "error" ? "Unavailable" : null);
        MobileProgress progress = new("Following mainnet", HeadFresh: true, BalanceAtHead: true,
            Balance: new("1", 42, "hash", "root", DateTimeOffset.UtcNow, 1), Tokens: state == "missing" ? [] : [token],
            Settings: new("account", false, false, true));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.IsSynced, Is.EqualTo(state == "ready"));
            Assert.That(progress.IsCurrent(token), Is.EqualTo(state is "ready" or "missing"));
        }
    }
}
