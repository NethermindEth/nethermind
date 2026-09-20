// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Core.Buffers;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class TransactionTests
{
    [Test, NonParallelizable]
    public void Concurrent_returns_of_wrapper_copies_do_not_reissue_the_same_buffer_twice()
    {
        byte[] data = new byte[PooledBlobBuffers.BlobSize];
        // Drain the bounded pool so duplicate returns cannot be hidden by a full bucket.
        byte[][] held = new byte[PooledBlobBuffers.MaxRetainedBlobs][];
        for (int i = 0; i < held.Length; i++) held[i] = PooledBlobBuffers.Copy(data);
        byte[][] blobs = [PooledBlobBuffers.Copy(data)];
        ShardBlobNetworkWrapper wrapper = new(blobs, [], [], ProofVersion.V0)
        {
            PooledBuffers = new(blobs)
        };
        Transaction first = new() { NetworkWrapper = wrapper };
        Transaction second = new() { NetworkWrapper = wrapper with { Version = ProofVersion.V1 } };
        Parallel.Invoke(() => PooledBlobBuffers.Return(first), () => PooledBlobBuffers.Return(second));

        byte[] firstRental = PooledBlobBuffers.Copy(data);
        byte[] secondRental = PooledBlobBuffers.Copy(data);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstRental, Is.SameAs(blobs[0]));
            Assert.That(secondRental, Is.Not.SameAs(firstRental));
        }
        new PooledBlobBuffers([firstRental, secondRental]).Return();
        new PooledBlobBuffers(held).Return();
    }

    [Test]
    public void Returning_a_copied_transaction_keeps_shared_blob_data([Values] bool copyHash)
    {
        byte[] data = new byte[PooledBlobBuffers.BlobSize];
        Array.Fill(data, (byte)0x11);
        byte[][] blobs = [PooledBlobBuffers.Copy(data)];
        Transaction source = new()
        {
            NetworkWrapper = new ShardBlobNetworkWrapper(blobs, [], [], ProofVersion.V0)
            {
                PooledBuffers = new(blobs)
            }
        };
        Transaction copy = new();
        source.CopyTo(copy, copyHash);
        new Transaction.PoolPolicy().Return(source);

        Array.Fill(data, (byte)0x22);
        byte[] next = PooledBlobBuffers.Copy(data);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(next, Is.Not.SameAs(blobs[0]));
            Assert.That(((ShardBlobNetworkWrapper)copy.NetworkWrapper!).Blobs[0], Is.All.EqualTo(0x11));
        }
        new PooledBlobBuffers([next]).Return();
    }

    [Test]
    public void CopyTo_should_preserve_legacy_hash_behavior_and_expose_explicit_hash_control()
    {
        MethodInfo? legacyCopy = typeof(Transaction).GetMethod(nameof(Transaction.CopyTo), [typeof(Transaction)]);
        MethodInfo? explicitCopy = typeof(Transaction).GetMethod(nameof(Transaction.CopyTo), [typeof(Transaction), typeof(bool)]);
        Transaction source = new() { Hash = TestItem.KeccakA };
        Transaction legacyDestination = new() { Hash = TestItem.KeccakB };
        Transaction hashPreservingDestination = new();

        source.CopyTo(legacyDestination);
        source.CopyTo(hashPreservingDestination, copyHash: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(legacyCopy, Is.Not.Null);
            Assert.That(explicitCopy, Is.Not.Null);
            Assert.That(explicitCopy?.GetParameters()[1].HasDefaultValue, Is.False);
            Assert.That(legacyDestination.Hash, Is.EqualTo(TestItem.KeccakB));
            Assert.That(hashPreservingDestination.Hash, Is.EqualTo(source.Hash));
        }
    }

    [Test]
    public void ShardBlobNetworkWrapper_should_preserve_legacy_constructor_and_deconstructor()
    {
        Type wrapperType = typeof(ShardBlobNetworkWrapper);
        Type[] constructorParameters = [typeof(byte[][]), typeof(byte[][]), typeof(byte[][]), typeof(ProofVersion)];
        Type[] deconstructorParameters =
        [
            typeof(byte[][]).MakeByRefType(),
            typeof(byte[][]).MakeByRefType(),
            typeof(byte[][]).MakeByRefType(),
            typeof(ProofVersion).MakeByRefType(),
        ];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapperType.GetConstructor(constructorParameters), Is.Not.Null);
            Assert.That(wrapperType.GetMethod("Deconstruct", deconstructorParameters), Is.Not.Null);
        }
    }

    [Test]
    public void When_to_not_empty_then_is_message_call()
    {
        Transaction transaction = new();
        transaction.To = Address.Zero;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.IsMessageCall, Is.True, nameof(Transaction.IsMessageCall));
            Assert.That(transaction.IsContractCreation, Is.False, nameof(Transaction.IsContractCreation));
        }
    }

    [Test]
    public void When_to_empty_then_is_message_call()
    {
        Transaction transaction = new();
        transaction.To = null;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.IsMessageCall, Is.False, nameof(Transaction.IsMessageCall));
            Assert.That(transaction.IsContractCreation, Is.True, nameof(Transaction.IsContractCreation));
        }
    }

    [TestCase(1, true)]
    [TestCase(300, true)]
    public void Supports1559_returns_expected_results(int decodedFeeCap, bool expectedSupports1559)
    {
        Transaction transaction = new();
        transaction.DecodedMaxFeePerGas = (uint)decodedFeeCap;
        transaction.Type = TxType.EIP1559;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.DecodedMaxFeePerGas, Is.EqualTo(transaction.MaxFeePerGas));
            Assert.That(transaction.Supports1559, Is.EqualTo(expectedSupports1559));
        }
    }
}
