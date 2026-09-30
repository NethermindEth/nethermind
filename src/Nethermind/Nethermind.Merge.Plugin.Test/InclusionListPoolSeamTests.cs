// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Pins the seam between the pool's non-frame readiness filter and the run the inclusion list builder rebuilds
/// from a vouched-for bucket: the two judge the same bucket by the same rule, in different code.
/// </summary>
/// <remarks>
/// Both sides are the production ones — a <see cref="TxPool"/> wired by the production modules, admitting real
/// transactions, and a real <see cref="InclusionListBuilder"/> — so a readiness dimension gained by one and not
/// the other turns one of the two assertions red instead of silently shortening the list.
/// </remarks>
public class InclusionListPoolSeamTests
{
    /// <summary>What the sender's bucket holds; the account nonce is always 0.</summary>
    public enum Bucket
    {
        KeyedFrameOnly,
        OrdinaryAtTheNonce,
        KeyedFrameAndOrdinaryAtTheNonce,
        KeyedFrameAndOrdinaryAhead,
        OrdinaryAhead,
        OrdinaryAtTheNonceButPricedOut,
        AccountDomainFrameAtTheNonce,
    }

    private static readonly ISpecProvider PoolSpecProvider =
        new TestSpecProvider(new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip8250Enabled = true });

    /// <summary>Genesis funds this one without code, so the pool admits transactions from it.</summary>
    private static readonly PrivateKey SenderKey = TestItem.PrivateKeyB;

    [TestCase(Bucket.KeyedFrameOnly, false)]
    [TestCase(Bucket.OrdinaryAtTheNonce, true)]
    [TestCase(Bucket.KeyedFrameAndOrdinaryAtTheNonce, true)]
    [TestCase(Bucket.KeyedFrameAndOrdinaryAhead, false)]
    [TestCase(Bucket.OrdinaryAhead, false)]
    [TestCase(Bucket.OrdinaryAtTheNonceButPricedOut, false)]
    // The one shape the two filters disagree on for a reason other than keyed reads: SupportsFrames skips it,
    // UsesKeyedNonce would not, so HasReadyTransaction vouches for a bucket the builder still strips to nothing.
    [TestCase(Bucket.AccountDomainFrameAtTheNonce, false)]
    public async Task Pool_vouches_for_exactly_the_buckets_the_builder_can_draw_from(Bucket bucket, bool expectedReady)
    {
        UInt256 nextBaseFee = bucket == Bucket.OrdinaryAtTheNonceButPricedOut ? 2 : UInt256.Zero;
        // The prefix simulator is the one production component overridden: these frame transactions stand for a
        // bucket shape, and no EVM-executable prefix is needed to judge readiness.
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(PoolSpecProvider)
            .AddSingleton(AcceptingPrefixSimulator()));
        EthereumEcdsa ecdsa = new(chain.SpecProvider.ChainId);

        foreach (Transaction tx in TransactionsOf(bucket, ecdsa))
        {
            Assert.That(chain.TxPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        }

        // Frontier leaves the parent's base fee alone, so this header fixes the fee both sides are judged at.
        BlockHeader parent = Build.A.BlockHeader.WithNumber(1).WithBaseFee(nextBaseFee).TestObject;
        bool vouched = chain.TxPool.GetPendingTransactionsBySenderWithReadyNonFrameTx(nextBaseFee)
            .ContainsKey(SenderKey.Address);

        using InclusionListBytes drawn = BuildBuilder(chain.TxPool, chain).GetInclusionList(parent);
        using InclusionListBytes drawnIfVouchedForAnyway =
            BuildBuilder(VouchingFor(chain.TxPool.GetPendingTransactionsBySender()), chain).GetInclusionList(parent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(vouched, Is.EqualTo(expectedReady));
            Assert.That(drawnIfVouchedForAnyway.Count > 0, Is.EqualTo(vouched),
                "the pool vouches for a bucket the builder draws nothing from, or drops one it could have used");
            Assert.That(drawn.Count > 0, Is.EqualTo(vouched), "the list holds what the pool vouched for");
        }
    }

    private static IEnumerable<Transaction> TransactionsOf(Bucket bucket, EthereumEcdsa ecdsa)
    {
        if (bucket is Bucket.KeyedFrameOnly or Bucket.KeyedFrameAndOrdinaryAtTheNonce or Bucket.KeyedFrameAndOrdinaryAhead)
        {
            yield return FrameTxWithNonceKeys([1]);
        }
        else if (bucket is Bucket.AccountDomainFrameAtTheNonce)
        {
            // [0] aliases the account nonce, so this frame tx is account-domain: UsesKeyedNonce is false for it.
            yield return FrameTxWithNonceKeys([0]);
        }

        switch (bucket)
        {
            case Bucket.OrdinaryAtTheNonce or Bucket.KeyedFrameAndOrdinaryAtTheNonce:
                yield return OrdinaryTx(ecdsa, nonce: 0, maxFee: 1.GWei);
                break;
            case Bucket.KeyedFrameAndOrdinaryAhead or Bucket.OrdinaryAhead:
                yield return OrdinaryTx(ecdsa, nonce: 2, maxFee: 1.GWei);
                break;
            case Bucket.OrdinaryAtTheNonceButPricedOut:
                yield return OrdinaryTx(ecdsa, nonce: 0, maxFee: 1);
                break;
        }
    }

    private static Transaction OrdinaryTx(EthereumEcdsa ecdsa, ulong nonce, UInt256 maxFee) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithNonce(nonce)
            .WithChainId(PoolSpecProvider.ChainId)
            .WithGasLimit(21_000)
            .WithMaxFeePerGas(maxFee)
            .WithMaxPriorityFeePerGas(maxFee)
            .SignedAndResolved(ecdsa, SenderKey).TestObject;

    /// <remarks>A frame transaction authenticates by its frame signatures, so it carries a sender rather than an
    /// outer signature.</remarks>
    private static Transaction FrameTxWithNonceKeys(UInt256[] nonceKeys)
    {
        Transaction tx = FrameTxTestFrames.FrameTx(
            SenderKey.Address, [], FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas));
        tx.ChainId = PoolSpecProvider.ChainId;
        tx.Nonce = 0;
        tx.NonceKeys = nonceKeys;
        tx.GasLimit = 1_000_000;
        tx.GasPrice = 1.GWei;
        tx.DecodedMaxFeePerGas = 1.GWei;
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static IFrameTxPrefixSimulator AcceptingPrefixSimulator()
    {
        IFrameTxPrefixSimulator simulator = Substitute.For<IFrameTxPrefixSimulator>();
        simulator.Simulate(Arg.Any<Transaction>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<Func<bool>?>())
            .Returns(FrameTxSimulationResult.Accept(SenderKey.Address));
        return simulator;
    }

    /// <summary>A pool that vouches for every bucket, to show what the builder would have drawn from one.</summary>
    private static ITxPool VouchingFor(IDictionary<AddressAsKey, Transaction[]> buckets)
    {
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactionsBySenderWithReadyNonFrameTx(Arg.Any<UInt256>()).Returns(buckets);
        return pool;
    }

    private static InclusionListBuilder BuildBuilder(ITxPool pool, BasicTestBlockchain chain) =>
        new(pool, chain.BlockTree, new TestSpecProvider(Frontier.Instance), chain.ReadOnlyState);
}
