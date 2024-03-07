// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class CodeTests
{
    [Test]
    public void TestByteCodeIsSameAsReconstructedVerkleCode()
    {
        TestSpecProvider specProvider = new (Prague.Instance);
        VerkleStateTree tree = TestItem.Tree.GetVerkleStateTree(null);
        MemDb codeDb = new ();
        VerkleWorldState worldState = new (tree, codeDb, LimboLogs.Instance);

        byte[] code = new byte[2000];
        TestItem.Random.NextBytes(code);

        worldState.CreateAccount(TestItem.AddressA, 200000);
        worldState.Commit(specProvider.SpecToReturn);
        worldState.InsertCode(TestItem.AddressA, code, specProvider.SpecToReturn, false);
        worldState.Commit(specProvider.SpecToReturn);
        worldState.CommitTree(0);

        CodeInfo byteCode = new (code);
        CodeInfo verkleCode = new (worldState, TestItem.AddressA);

        Assert.IsTrue(byteCode.MachineCode.Length == verkleCode.MachineCode.Length);

        byteCode.MachineCode.Span.ToArray().Should().BeEquivalentTo(verkleCode.MachineCode.Span.ToArray());
    }



    [Test]
    public void TestByteCodeIsSameAsReconstructedVerkleCodeWithIncompleteCode()
    {
        TestSpecProvider specProvider = new (Prague.Instance);
        VerkleStateTree tree = TestItem.Tree.GetVerkleStateTree(null);
        MemDb codeDb = new ();
        VerkleWorldState worldState = new (tree, codeDb, LimboLogs.Instance);

        byte[] code = new byte[2000];
        TestItem.Random.NextBytes(code);

        worldState.CreateAccount(TestItem.AddressA, 200000);
        worldState.Commit(specProvider.SpecToReturn);

        InsertAlternateChunks(code, tree);
        worldState.CommitTree(0);

        CodeInfo byteCode = new (code);
        CodeInfo verkleCode = new (worldState, TestItem.AddressA);

        Assert.That(byteCode.MachineCode.Length, Is.EqualTo(verkleCode.MachineCode.Length));

        {
            Span<byte> emptyCodeSlice = new Byte[31];
            ReadOnlySpan<byte> byteCodeSpan = byteCode.MachineCode.Span;
            ReadOnlySpan<byte> verkleCodeSpan = verkleCode.MachineCode.Span;
            int index = 0;
            bool skip = true;
            while (index < code.Length)
            {
                int endIndex = index + 31;
                if (endIndex > code.Length) endIndex = code.Length;

                ReadOnlySpan<byte> verkleCodeSlice = verkleCodeSpan[index..endIndex];
                if (skip)
                {
                    skip = false;
                    Assert.That(Bytes.SpanEqualityComparer.Equals(verkleCodeSlice, emptyCodeSlice[..verkleCodeSlice.Length]), Is.True);
                }
                else
                {
                    skip = true;
                    ReadOnlySpan<byte> byteCodeSlice = byteCodeSpan[index..endIndex];
                    Assert.That(Bytes.SpanEqualityComparer.Equals(verkleCodeSlice, byteCodeSlice), Is.True);
                }
                index += 31;
            }
        }
    }

    private void InsertAlternateChunks(byte[] code, VerkleStateTree tree)
    {

        UInt256 chunkId = 0;
        var codeEnumerator = new CodeChunkEnumerator(code);
        bool skip = true;
        while (codeEnumerator.TryGetNextChunk(out byte[] chunk))
        {
            if (skip)
            {
                skip = false;
                chunkId += 1;
                continue;
            }

            skip = true;
            Hash256? key = AccountHeader.GetTreeKeyForCodeChunk(TestItem.AddressA.Bytes, chunkId);
            tree.Insert(key, chunk);
            chunkId += 1;
        }

        tree.Set(TestItem.AddressA, new Account(0, 200000, Keccak.EmptyTreeHash, Keccak.MaxValue){CodeSize = 2000});
        tree.Commit();
    }

}
