// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

namespace Nethermind.TxPool.Test;

/// <summary>The calldata a frame transaction is priced on, for the transactions that never met the decoder.
/// The filter rejects nothing, so every case is about what it measured.</summary>
internal class FrameTxCalldataStatsFilterTests
{
    // The expected counts are the bytes the fields encode to, spelled out rather than taken from the
    // measurement under test: nonce_keys as a list, then the sequence number.
    [TestCase(1ul, 0, 3, TestName = "Accept_MeasuresASingleByteNonceKey")]
    [TestCase(0x0100ul, 1, 4, TestName = "Accept_CountsTheZeroByteOfATwoByteNonceKey")]
    public void Accept_MeasuresTheNonceKeyCalldataOfALocallyBuiltTransaction(ulong nonceKey, int zeroBytes, int nonZeroBytes)
    {
        Transaction tx = FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas));
        tx.NonceKeys = [(UInt256)nonceKey];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.FrameCalldataStats, Is.EqualTo((ZeroBytes: 0, NonZeroBytes: 0)),
                "a field-built transaction starts unmeasured, or the case proves nothing");
            Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(tx.FrameCalldataStats, Is.EqualTo((zeroBytes, nonZeroBytes)));
        }
    }

    // One reference of all-0xff hashes and a one-byte slot encodes to 71 bytes with no zero among them.
    [Test]
    public void Accept_MeasuresTheRecentRootReferenceCalldataOfALocallyBuiltTransaction()
    {
        Transaction tx = FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas));
        tx.RecentRootReferences = [Reference()];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tx.ReferenceCalldataStats, Is.EqualTo((ZeroBytes: 0, NonZeroBytes: 0)),
                "a field-built transaction starts unmeasured, or the case proves nothing");
            Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(tx.ReferenceCalldataStats, Is.EqualTo((ZeroBytes: 0, NonZeroBytes: 71)));
        }
    }

    // The decoder measures off the wire. A transaction built field by field must reach the same reading,
    // or the pool and the processor price the same transaction differently.
    [Test]
    public void Accept_ReachesTheReadingTheDecoderTakesOffTheWire()
    {
        Transaction built = FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas));
        built.ChainId = TestBlockchainIds.ChainId;
        built.NonceKeys = [UInt256.One, (UInt256)0x0100];
        built.RecentRootReferences = [Reference()];

        Transaction decoded = TxDecoderRoundtrip.Roundtrip(built);
        Accept(built);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.FrameCalldataStats.NonZeroBytes, Is.GreaterThan(0), "the decoder must have measured something");
            Assert.That(built.FrameCalldataStats, Is.EqualTo(decoded.FrameCalldataStats));
            Assert.That(built.ReferenceCalldataStats, Is.EqualTo(decoded.ReferenceCalldataStats));
        }
    }

    [Test]
    public void Accept_LeavesTheNonceCalldataOfALegacyNonceTransactionUnmeasured()
    {
        // Without a key set there is no nonce calldata to charge for; measuring one would price a field
        // the transaction does not carry.
        Transaction tx = FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas));

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(tx.FrameCalldataStats, Is.EqualTo((ZeroBytes: 0, NonZeroBytes: 0)));
    }

    [Test]
    public void Accept_LeavesANonFrameTransactionAlone()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithSenderAddress(TestItem.AddressA).TestObject;
        tx.RecentRootReferences = [Reference()];

        Assert.That(Accept(tx), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(tx.ReferenceCalldataStats, Is.EqualTo((ZeroBytes: 0, NonZeroBytes: 0)));
    }

    [Test]
    public void Accept_MakesThePoolPriceTheCalldataTheProcessorPrices()
    {
        // The pool prices a field-built transaction before this filter runs, so an unmeasured reading would
        // be memoized and every derived bound would under-count.
        IReleaseSpec spec = ReleaseSpecSubstitute.Create();
        spec.IsEip8250Enabled.Returns(true);
        spec.IsEip8272Enabled.Returns(true);
        Transaction tx = FrameTx(TestItem.AddressA, [], SelfVerify(PrefixFrameGas));
        tx.NonceKeys = [UInt256.One];
        tx.RecentRootReferences = [Reference()];

        Assert.That(FrameTxValidation.TryCalculateGasBudget(tx, spec, out ulong unmeasured, out _, out _), Is.True);
        Accept(tx);

        Assert.That(FrameTxValidation.TryCalculateGasBudget(tx, spec, out ulong measured, out _, out _), Is.True);
        Assert.That(measured, Is.GreaterThan(unmeasured));
    }

    /// <summary>A reference whose every byte is non-zero, so its encoded length is its non-zero count.</summary>
    private static RecentRootReference Reference() =>
        new(ValueKeccak.MaxValue, slot: 1, ValueKeccak.MaxValue);

    private static AcceptTxResult Accept(Transaction tx)
    {
        FrameTxCalldataStatsFilter filter = new();
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);
        return filter.Accept(tx, ref state, TxHandlingOptions.None);
    }
}
