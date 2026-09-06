// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

public class GuestHashSeedTests
{
    /// <remarks>
    /// The saving this guards is a property of the whole type, not of one file: a single static field
    /// initializer in any partial compiled into the guest re-emits the class constructor and puts a
    /// class-initialisation check, a fence and a two-level static load back on every mixer call - about
    /// 134,000 of them per block. Nothing else in CI would notice, since the guest job compares output
    /// bytes rather than step counts.
    /// </remarks>
    [Test]
    public void Guest_hash_type_has_no_class_constructor() =>
        Assert.That(typeof(SpanExtensions).TypeInitializer, Is.Null);

    /// <remarks>
    /// EIP-8025: the guest has no entropy source, so a colliding key set found offline is only useless
    /// against the next payload if that payload's root reaches the hash. It has to reach the lane
    /// products rather than the combined result - applied after the lanes are combined it cancels in
    /// the difference between two keys, and one offline set would hold for every root.
    /// </remarks>
    [Test]
    public void Guest_mixer_hashes_a_key_differently_under_a_different_payload_root(
        [Values(Address.Size, ValueHash256.MemorySize)] int width)
    {
        byte[] key = new byte[width];
        key.AsSpan().Fill(0xAB);

        try
        {
            Assert.That(HashUnderRoot(RootOf(1), key), Is.Not.EqualTo(HashUnderRoot(RootOf(2), key)));
        }
        finally
        {
            SpanExtensions.SeedHashes(SeedGuestHashes.Seed);
        }

        static long HashUnderRoot(in ValueHash256 root, byte[] key)
        {
            SpanExtensions.SeedHashes(root);
            ref byte start = ref MemoryMarshal.GetArrayDataReference(key);
            return key.Length == Address.Size
                ? SpanExtensions.FastHash64For20BytesFallback(ref start)
                : SpanExtensions.FastHash64For32BytesFallback(ref start);
        }
    }

    /// <remarks>
    /// <see cref="UInt256"/> comes from a package whose guest build seeds its own hash from a
    /// compile-time constant, so the storage-slot maps keyed by it are covered only as long as
    /// <see cref="GenericEqualityComparer{T}"/> keeps routing them through the mixer instead. That the
    /// mixer then follows the payload root is the test above; this one cannot show it directly, because
    /// a ZK build on a host with AES takes the AES path, which the guest's riscv64 target never has.
    /// </remarks>
    [Test]
    public void Guest_slot_comparer_hashes_through_the_mixer()
    {
        UInt256 slot = new(0xAB, 0xCD, 0xEF, 0x01);
        int hash = GenericEqualityComparer<UInt256>.Default.GetHashCode(slot);

        Assert.Multiple(() =>
        {
            Assert.That(hash, Is.EqualTo(unchecked((int)SpanExtensions.FastHash64For32Bytes(
                ref Unsafe.As<UInt256, byte>(ref slot)))));
            Assert.That(hash, Is.Not.EqualTo(slot.GetHashCode()));
        });
    }

    private static ValueHash256 RootOf(byte fill)
    {
        Span<byte> bytes = stackalloc byte[ValueHash256.MemorySize];
        bytes.Fill(fill);
        return new ValueHash256(bytes);
    }
}
