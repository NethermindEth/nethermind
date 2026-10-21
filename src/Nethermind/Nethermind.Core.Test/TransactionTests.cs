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
    [Test]
    public void Wrapper_equality_ignores_pool_ownership()
    {
        ShardBlobNetworkWrapper wrapper = new([[1]], [[2]], [[3]], ProofVersion.V0);
        ShardBlobNetworkWrapper pooled = wrapper with { PooledBuffers = new(wrapper.Blobs) };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pooled.Equals(pooled), Is.True);
            Assert.That(pooled.Equals(wrapper), Is.True);
            Assert.That(wrapper.Equals(pooled), Is.True);
            Assert.That(pooled.GetHashCode(), Is.EqualTo(wrapper.GetHashCode()));
            Assert.That(pooled == wrapper, Is.True);
            Assert.That(pooled.Equals(wrapper with { Blobs = [[1]] }), Is.False);
            Assert.That(pooled.Equals(wrapper with { Commitments = [[2]] }), Is.False);
            Assert.That(pooled.Equals(wrapper with { Proofs = [[3]] }), Is.False);
            Assert.That(pooled.Equals(wrapper with { Version = ProofVersion.V1 }), Is.False);
            Assert.That(pooled.Equals(wrapper with { CellMask = BlobCellMask.Full }), Is.False);
            Assert.That(pooled.Equals(wrapper with { Cells = [[4]] }), Is.False);
            Assert.That(pooled.Equals(null), Is.False);
        }
    }

    [Test, NonParallelizable]
    public void Returns_of_wrapper_copies_do_not_reissue_the_same_buffer_twice([Values] bool concurrent)
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
        if (concurrent)
        {
            Parallel.Invoke(() => PooledBlobBuffers.Return(first), () => PooledBlobBuffers.Return(second));
        }
        else
        {
            PooledBlobBuffers.Return(first);
            PooledBlobBuffers.Return(first);
            PooledBlobBuffers.Return(second);
        }

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

    [Test]
    public void FrameTx_type_value_is_0x06() => Assert.That((byte)TxType.FrameTx, Is.EqualTo(0x06));

    [Test]
    public void FrameFields_OnNewTransaction_AreNull()
    {
        Transaction transaction = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Frames, Is.Null, nameof(Transaction.Frames));
            Assert.That(transaction.FrameSignatures, Is.Null, nameof(Transaction.FrameSignatures));
        }
    }

    [TestCase(TxType.Legacy, false, TestName = "Legacy")]
    [TestCase(TxType.AccessList, false, TestName = "AccessList")]
    [TestCase(TxType.EIP1559, false, TestName = "EIP1559")]
    [TestCase(TxType.Blob, false, TestName = "Blob")]
    [TestCase(TxType.SetCode, false, TestName = "SetCode")]
    [TestCase(TxType.FrameTx, true, TestName = "FrameTx")]
    [TestCase(TxType.DepositTx, false, TestName = "DepositTx")]
    public void SupportsFrames_PerTxType_MatchesExpectation(TxType txType, bool expected)
    {
        Transaction transaction = new() { Type = txType };
        Assert.That(transaction.SupportsFrames, Is.EqualTo(expected));
    }

    [Test]
    public void FrameTx_TypePredicates_MatchEip8141Payload()
    {
        Transaction transaction = new() { Type = TxType.FrameTx };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Supports1559, Is.True, "frame txs carry EIP-1559 fee fields");
            Assert.That(transaction.SupportsAccessList, Is.False, "frame txs have no access list field");
            Assert.That(transaction.SupportsAuthorizationList, Is.False, "frame txs have no authorization list");
            Assert.That(transaction.SupportsBlobs, Is.False, "frame tx blob handling keys on blob presence, not the type");
        }
    }

    // The compensating invariant for SupportsBlobs being type-3-only: a blob-carrying frame tx must
    // still be recognised as carrying blobs, or it would slip past the node's blob paths.
    [TestCase(null, false, TestName = "FrameTx_CarriesBlobs_AbsentHashList")]
    [TestCase(0, false, TestName = "FrameTx_CarriesBlobs_EmptyHashList")]
    [TestCase(2, true, TestName = "FrameTx_CarriesBlobs_PopulatedHashList")]
    public void FrameTx_CarriesBlobs_TracksBlobPresenceNotType(int? blobCount, bool expected)
    {
        byte[]?[]? blobVersionedHashes = blobCount is null ? null : new byte[blobCount.Value][];
        for (int i = 0; i < (blobCount ?? 0); i++)
        {
            blobVersionedHashes![i] = new byte[32];
        }

        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            BlobVersionedHashes = blobVersionedHashes
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.SupportsBlobs, Is.False);
            Assert.That(transaction.CarriesBlobs, Is.EqualTo(expected));
            Assert.That(transaction.GetBlobCount(), Is.EqualTo(blobCount ?? 0));
        }
    }

    [Test]
    public void CopyTo_WithFrameFields_CopiesFrames()
    {
        Transaction source = new()
        {
            Type = TxType.FrameTx,
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null, 50_000, 0, default)],
            FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, null, default, new byte[65])],
        };
        Transaction destination = new();

        source.CopyTo(destination);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(destination.Frames, Is.SameAs(source.Frames), nameof(Transaction.Frames));
            Assert.That(destination.FrameSignatures, Is.SameAs(source.FrameSignatures), nameof(Transaction.FrameSignatures));
        }
    }
}
