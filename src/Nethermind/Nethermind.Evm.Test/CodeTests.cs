// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
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
    public void ICodeTests()
    {
        TestSpecProvider specProvider = new (Prague.Instance);
        VerkleStateTree tree = TestItem.Tree.GetVerkleStateTree(null);
        MemDb codeDb = new ();
        VerkleWorldState worldState = new (tree, codeDb, LimboLogs.Instance);

        byte[] code = new byte[2000];
        TestItem.Random.NextBytes(code);

        worldState.CreateAccount(TestItem.AddressA, 200000);
        worldState.CreateAccount(TestItem.AddressB, 200000);
        worldState.Commit(specProvider.SpecToReturn);
        worldState.InsertCode(TestItem.AddressA, code, specProvider.SpecToReturn, false);
        worldState.Commit(specProvider.SpecToReturn);
        worldState.CommitTree(0);

        ByteCode byteCode = new (code);
        VerkleCode verkleCode = new (worldState, TestItem.AddressA);

        Assert.IsTrue(byteCode.Length == verkleCode.Length);

        byte[] byteCodeBytes = byteCode.ToBytes();
        byte[] verkleCodeBytes = verkleCode.ToBytes();
        byteCodeBytes.Should().BeEquivalentTo(verkleCodeBytes);

        for (int i = 0; i < code.Length; i++) AssetEqualSlice(byteCode, verkleCode, i, code.Length);
        for (int i = 0; i < code.Length; i++) AssetEqualZeroPaddedSpan(byteCode, verkleCode, i, code.Length);
    }

    private void AssetEqualSlice(ByteCode byteCode, VerkleCode verkleCode, int start, int length)
    {
        Span<byte> byteCodeSlice = byteCode.Slice(start, Math.Min(length - start, length));
        Span<byte> verkleCodeSlice = verkleCode.Slice(start, Math.Min(length - start, length));
        byteCodeSlice.ToArray().Should().BeEquivalentTo(verkleCodeSlice.ToArray());
    }

    private void AssetEqualZeroPaddedSpan(ByteCode byteCode, VerkleCode verkleCode, int start, int length)
    {
        ZeroPaddedSpan byteCodeSlice = byteCode.SliceWithZeroPadding((UInt256)start, length);
        ZeroPaddedSpan verkleCodeSlice = verkleCode.SliceWithZeroPadding((UInt256)start, length);
        Assert.IsTrue(byteCodeSlice.Length == verkleCodeSlice.Length);
        Assert.IsTrue(byteCodeSlice.PaddingLength == verkleCodeSlice.PaddingLength);
        Assert.IsTrue(byteCodeSlice.PadDirection == verkleCodeSlice.PadDirection);
        Assert.IsTrue(byteCodeSlice.Span.SequenceEqual(verkleCodeSlice.Span));
    }
}
