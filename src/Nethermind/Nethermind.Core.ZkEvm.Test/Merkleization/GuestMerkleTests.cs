// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Merkleization;

/// <summary>
/// The ZisK guest registers a merkle pair hasher built on its SHA-256 compression precompile, which no
/// ordinary machine has, and the generic SHA-256 accelerator the other guests keep cannot run here either.
/// These register host stand-ins instead, so they pin the registration check and that merkleization goes
/// through whatever passed it.
/// </summary>
// The registration is process-wide.
[NonParallelizable]
public class GuestMerkleTests
{
    private static int _standInCalls;
    private static Fault _fault;

    public enum Fault
    {
        SwapsChunks,
        FailsAdjacentChunks,
        FailsSeparateChunks,
        ClearsParentFirst,
    }

    [Test]
    public unsafe void Merkleize_hashes_through_an_accelerator_that_passes_the_check()
    {
        Assert.That(Merkle.TryRegisterHashPairAccelerator(&StandIn), Is.True);
        int calls = _standInCalls;
        UInt256[] chunks = [1, 2, 3];

        Merkle.Merkleize(out UInt256 root, chunks);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_standInCalls, Is.EqualTo(calls + 3));
            Assert.That(root, Is.EqualTo(Sha256(Sha256(chunks[0], chunks[1]), Sha256(chunks[2], UInt256.Zero))));
        }
    }

    [Test]
    public unsafe void An_accelerator_that_mishashes_a_pair_is_refused([Values] Fault fault)
    {
        _fault = fault;

        Assert.That(Merkle.TryRegisterHashPairAccelerator(&Faulty), Is.False);
    }

    private static void StandIn(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        _standInCalls++;
        parent = Sha256(left, right);
    }

    private static void Faulty(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        bool adjacent = Unsafe.AreSame(ref Unsafe.Add(ref Unsafe.AsRef(in left), 1), ref Unsafe.AsRef(in right));
        if (_fault == Fault.ClearsParentFirst)
        {
            parent = default;
        }

        parent = _fault switch
        {
            Fault.SwapsChunks => Sha256(right, left),
            Fault.FailsAdjacentChunks when adjacent => default,
            Fault.FailsSeparateChunks when !adjacent => default,
            _ => Sha256(left, right),
        };
    }

    private static UInt256 Sha256(in UInt256 left, in UInt256 right)
    {
        Span<UInt256> concatenation = [left, right];
        UInt256 hash = default;
        SHA256.HashData(MemoryMarshal.AsBytes(concatenation), MemoryMarshal.AsBytes(new Span<UInt256>(ref hash)));
        return hash;
    }
}
