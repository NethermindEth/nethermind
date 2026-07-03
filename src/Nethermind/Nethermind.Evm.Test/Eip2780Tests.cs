// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-2780 reprices the value-moving call cost and cold-account touches. These tests pin the
/// gas-policy primitives to the EIP's reference values and exercise the intrinsic path end-to-end.
/// </summary>
[TestFixture]
public class Eip2780Tests
{
    private static readonly IReleaseSpec Eip2780Spec = new OverridableReleaseSpec(Prague.Instance) { IsEip2780Enabled = true };

    private static ulong ChargeCallValue(bool isSelfCall, bool recipientEmpty)
    {
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(1_000_000);
        Assert.That(EthereumGasPolicy.ConsumeCallValueTransferEip2780(ref gas, isSelfCall, recipientEmpty, Eip2780Spec), Is.True);
        return 1_000_000 - EthereumGasPolicy.GetRemainingGas(in gas);
    }

    [TestCase(true, false, GasCostOf.CallValueSelfEip2780, TestName = "self-call → STATE_UPDATE (1000)")]
    [TestCase(false, false, GasCostOf.CallValueExistingEip2780, TestName = "existing recipient → 2*STATE_UPDATE + log (3756)")]
    [TestCase(false, true, GasCostOf.CallValueNewAccountEip2780, TestName = "empty recipient → STATE_UPDATE + NEW_ACCOUNT + log (27756)")]
    public void Call_value_cost_uses_eip2780_tiers(bool isSelfCall, bool recipientEmpty, ulong expected) =>
        Assert.That(ChargeCallValue(isSelfCall, recipientEmpty), Is.EqualTo(expected));

    [Test]
    public void Call_value_tiers_have_expected_absolute_values()
    {
        // Guards against the constants drifting from the EIP-2780 specification.
        Assert.That(GasCostOf.CallValueSelfEip2780, Is.EqualTo(1000));
        Assert.That(GasCostOf.CallValueExistingEip2780, Is.EqualTo(3756));
        Assert.That(GasCostOf.CallValueNewAccountEip2780, Is.EqualTo(27756));
    }

    private static ulong ChargeAccountAccess(IReleaseSpec spec, bool hasCode, bool prewarm)
    {
        EthereumGasPolicy gas = EthereumGasPolicy.FromULong(1_000_000);
        using StackAccessTracker tracker = new();
        if (prewarm) tracker.WarmUp(TestItem.AddressB);
        Assert.That(EthereumGasPolicy.ConsumeAccountAccessGas(ref gas, spec, in tracker, isTracingAccess: false, TestItem.AddressB, hasCode: hasCode), Is.True);
        return 1_000_000 - EthereumGasPolicy.GetRemainingGas(in gas);
    }

    [Test]
    public void Cold_account_touch_is_two_tier_under_eip2780()
    {
        Assert.That(ChargeAccountAccess(Eip2780Spec, hasCode: true, prewarm: false), Is.EqualTo(GasCostOf.ColdAccountAccess), "cold account with code");
        Assert.That(ChargeAccountAccess(Eip2780Spec, hasCode: false, prewarm: false), Is.EqualTo(GasCostOf.ColdAccountAccessNoCodeEip2780), "cold account without code");
        Assert.That(ChargeAccountAccess(Eip2780Spec, hasCode: false, prewarm: true), Is.EqualTo(GasCostOf.WarmStateRead), "warm account stays at WARM_STATE_READ");
    }

    [Test]
    public void Cold_account_touch_stays_flat_without_eip2780()
    {
        // Pre-EIP-2780 the code-less hint is ignored: every cold touch costs ColdAccountAccess.
        Assert.That(ChargeAccountAccess(Prague.Instance, hasCode: false, prewarm: false), Is.EqualTo(GasCostOf.ColdAccountAccess));
        Assert.That(ChargeAccountAccess(Prague.Instance, hasCode: true, prewarm: false), Is.EqualTo(GasCostOf.ColdAccountAccess));
    }

    private static Task<BasicTestBlockchain> CreateChain() =>
        BasicTestBlockchain.Create(b => b.AddSingleton<ISpecProvider>(
            new TestSpecProvider(new OverridableReleaseSpec(Prague.Instance) { IsEip2780Enabled = true, IsEip7708Enabled = true })));

    // Whole-transaction gas (base + recipient cold touch + value STATE_UPDATE + transfer log),
    // matching the EIP-2780 reference-case table; recipient AddressF is unfunded (dead per EIP-161).
    [TestCase(false, 1ul, GasCostOf.TransactionEip2780 + GasCostOf.ColdAccountAccessNoCodeEip2780 + GasCostOf.StateUpdateEip2780 + GasCostOf.TransferLogEip2780, TestName = "value transfer to existing EOA (7756)")]
    [TestCase(true, 1ul, GasCostOf.TransactionEip2780 + GasCostOf.ColdAccountAccessNoCodeEip2780 + GasCostOf.NewAccount + GasCostOf.TransferLogEip2780, TestName = "value transfer to new account (31756)")]
    [TestCase(false, 0ul, GasCostOf.TransactionEip2780 + GasCostOf.ColdAccountAccessNoCodeEip2780, TestName = "no-transfer to existing EOA (5000)")]
    [TestCase(true, 0ul, GasCostOf.TransactionEip2780 + GasCostOf.ColdAccountAccessNoCodeEip2780, TestName = "no-transfer to empty account (5000)")]
    public async Task Simple_transfer_spends_eip2780_total_gas(bool recipientIsNew, ulong value, ulong expectedGas)
    {
        using BasicTestBlockchain chain = await CreateChain();
        UInt256 nonce = chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, TestItem.AddressA);

        Address recipient = recipientIsNew ? TestItem.AddressF : TestItem.AddressB;
        Transaction tx = Build.A.Transaction
            .WithTo(recipient)
            .WithValue(value)
            .WithNonce((ulong)nonce)
            .WithGasLimit(60000)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        Block block = await chain.AddBlock(tx);

        Assert.That(chain.ReceiptStorage.Get(block)[0].GasUsed, Is.EqualTo(expectedGas));
    }
}
