// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class Blake3ManagedTests
{
    /// <summary>Sizes around BLAKE3 block, chunk, and chaining-value stack boundaries, plus the EIP-8297 node encoding lengths.</summary>
    private static IEnumerable<int> Sizes()
    {
        foreach (int size in new[] { 0, 1, 31, 32, 33, 63, 64, 65, 67, 99, 100, 127, 128, 192, 1023, 1024, 1025, 2048, 2049, 3072, 4096, 5000, 8192, 100_000 })
            yield return size;
    }

    [TestCaseSource(nameof(Sizes))]
    public void Matches_native_blake3(int size)
    {
        byte[] input = new byte[size];
        new Random(size).NextBytes(input);

        byte[] expected = new byte[32];
        global::Blake3.Hasher.Hash(input, expected);

        byte[] actual = new byte[32];
        Blake3Managed.Hash(input, actual);

        Assert.That(actual.ToHexString(), Is.EqualTo(expected.ToHexString()));
    }

    [Test]
    public void Two_inputs_match_single_hash(
        [Values(0, 1, 33, 64, 65, 67, 99, 128, 129, 1024, 1025)] int sizeA,
        [Values(0, 1, 33, 64, 65, 67, 99, 128, 129, 1024, 1025)] int sizeB)
    {
        byte[] inputA = new byte[sizeA];
        byte[] inputB = new byte[sizeB];
        new Random(sizeA).NextBytes(inputA);
        new Random(sizeB + 1).NextBytes(inputB);

        byte[] expectedA = new byte[32];
        byte[] expectedB = new byte[32];
        Blake3Managed.Hash(inputA, expectedA);
        Blake3Managed.Hash(inputB, expectedB);

        byte[] actualA = new byte[32];
        byte[] actualB = new byte[32];
        Blake3Managed.HashTwo(inputA, actualA, inputB, actualB);

        Assert.That(actualA.ToHexString(), Is.EqualTo(expectedA.ToHexString()));
        Assert.That(actualB.ToHexString(), Is.EqualTo(expectedB.ToHexString()));
    }

    /// <summary>Verifies the BLAKE3 empty-input reference vector independently of the native binding.</summary>
    [Test]
    public void Matches_reference_vector()
    {
        byte[] actual = new byte[32];
        Blake3Managed.Hash([], actual);
        Assert.That(actual.ToHexString(true), Is.EqualTo("0xaf1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262"));
    }
}
