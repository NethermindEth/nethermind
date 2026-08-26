// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class InclusionListTxSourceTests
{
    private static InclusionListTxSource CreateSource() => new(
        new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
        new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)),
        LimboLogs.Instance);

    private static PayloadAttributes Attributes(byte[][] inclusionList) => new() { InclusionListTransactions = inclusionList };

    private static IEnumerable<Transaction> GetTransactions(
        InclusionListTxSource source,
        PayloadAttributes payloadAttributes = null) =>
        source.GetTransactions(
            Build.A.BlockHeader.WithNumber(0).TestObject,
            Build.A.BlockHeader.WithNumber(1).TestObject,
            30_000_000UL,
            payloadAttributes);

    [Test]
    public void Empty_when_no_payload_attributes()
    {
        InclusionListTxSource source = CreateSource();

        Assert.That(GetTransactions(source), Is.Empty);
    }

    [Test]
    public void Empty_until_Set_is_called_for_the_build()
    {
        InclusionListTxSource source = CreateSource();
        Transaction tx = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[][] il = [Encode(tx)];
        PayloadAttributes attrs = Attributes(il);

        Assert.That(GetTransactions(source, attrs), Is.Empty);

        source.Set(il, Bogota.Instance);
        Assert.That(
            GetTransactions(source, attrs).Select(t => t.Nonce),
            Is.EqualTo([1ul]));
    }

    // Scoped by PayloadAttributes, so a concurrent FCU can't leak another build's IL.
    [Test]
    public void Inclusion_list_is_scoped_per_build()
    {
        InclusionListTxSource source = CreateSource();
        Transaction txA = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction txB = Build.A.Transaction.WithNonce(2).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        byte[][] ilA = [Encode(txA)];
        byte[][] ilB = [Encode(txB)];
        PayloadAttributes attrsA = Attributes(ilA);
        PayloadAttributes attrsB = Attributes(ilB);

        // Build A supplies its IL, then build B supplies another before A consumes its list.
        source.Set(ilA, Bogota.Instance);
        source.Set(ilB, Bogota.Instance);

        Assert.That(GetTransactions(source, attrsA).Select(t => t.Nonce), Is.EqualTo([1ul]));
        Assert.That(GetTransactions(source, attrsB).Select(t => t.Nonce), Is.EqualTo([2ul]));
    }

    [Test]
    public void Set_with_empty_array_yields_empty()
    {
        InclusionListTxSource source = CreateSource();
        byte[][] il = [];
        PayloadAttributes attrs = Attributes(il);

        source.Set(il, Bogota.Instance);
        Assert.That(GetTransactions(source, attrs), Is.Empty);
    }

    // Decoding and sender recovery must stay off the engine thread: a forkchoice update that is about to be
    // rejected, or that duplicates a build already under way, must cost nothing beyond retaining the list.
    [Test]
    public void Set_defers_sender_recovery_to_the_first_request()
    {
        CountingEcdsa ecdsa = new(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId));
        InclusionListTxSource source = new(ecdsa, new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)), LimboLogs.Instance);
        Transaction tx = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[][] il = [Encode(tx)];

        source.Set(il, Bogota.Instance);
        Assert.That(ecdsa.Recoveries, Is.Zero);

        Assert.That(GetTransactions(source, Attributes(il)), Is.Not.Empty);
        Assert.That(ecdsa.Recoveries, Is.EqualTo(1));
    }

    // Per spec, blob (EIP-4844) transactions are excluded from the inclusion list.
    [Test]
    public void SupportsBlobs_is_false() => Assert.That(CreateSource().SupportsBlobs, Is.False);

    // A blob IL entry must be dropped, never forwarded into block production.
    [Test]
    public void Blob_transactions_are_filtered_out()
    {
        InclusionListTxSource source = CreateSource();
        Transaction normal = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction blob = Build.A.Transaction
            .WithType(TxType.Blob)
            .WithGasLimit(100_000)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(10.GWei)
            .WithBlobVersionedHashes(1)
            .WithChainId(MainnetSpecProvider.Instance.ChainId)
            .WithNonce(2)
            .WithValue(UInt256.One)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        byte[][] il = [Encode(normal), Encode(blob)];
        PayloadAttributes attrs = Attributes(il);

        source.Set(il, Bogota.Instance);
        Assert.That(
            GetTransactions(source, attrs).Select(t => t.Nonce),
            Is.EqualTo([1ul]));
    }

    // The producer offers each IL tx once, so a shuffled list must still come out in ascending nonce order.
    [Test]
    public void Sender_nonces_are_ordered_ascending()
    {
        InclusionListTxSource source = CreateSource();
        Transaction nonce1 = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction nonce0 = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[][] il = [Encode(nonce1), Encode(nonce0)];
        PayloadAttributes attrs = Attributes(il);

        source.Set(il, Bogota.Instance);
        Assert.That(
            GetTransactions(source, attrs).Select(t => t.Nonce),
            Is.EqualTo([0ul, 1ul]));
    }

    // First-appearance order: sorting by address would favour low-address senders on a truncated list.
    [Test]
    public void Sender_order_of_first_appearance_is_preserved()
    {
        InclusionListTxSource source = CreateSource();
        Transaction b1 = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Transaction a1 = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction b0 = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        byte[][] il = [Encode(b1), Encode(a1), Encode(b0)];
        PayloadAttributes attrs = Attributes(il);

        source.Set(il, Bogota.Instance);
        Assert.That(
            GetTransactions(source, attrs).Select(t => (t.SenderAddress, t.Nonce)),
            Is.EqualTo([
                (TestItem.AddressB, 0ul),
                (TestItem.AddressB, 1ul),
                (TestItem.AddressA, 1ul)
            ]));
    }

    private static byte[] Encode(Transaction tx) => TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;

    private sealed class CountingEcdsa(IEthereumEcdsa inner) : IEthereumEcdsa
    {
        public int Recoveries;
        public ulong ChainId => inner.ChainId;

        public Address RecoverAddress(Signature signature, in ValueHash256 message)
        {
            Recoveries++;
            return inner.RecoverAddress(signature, in message);
        }

        public Signature Sign(PrivateKey privateKey, in ValueHash256 message) => inner.Sign(privateKey, in message);
        public PublicKey RecoverPublicKey(Signature signature, in ValueHash256 message) => inner.RecoverPublicKey(signature, in message);
        public CompressedPublicKey RecoverCompressedPublicKey(Signature signature, in ValueHash256 message) => inner.RecoverCompressedPublicKey(signature, in message);
    }
}
