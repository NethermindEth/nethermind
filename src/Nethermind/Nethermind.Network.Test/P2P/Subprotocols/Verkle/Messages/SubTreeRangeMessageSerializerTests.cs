// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Network.P2P.Subprotocols.Verkle.Messages;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.Utils;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Verkle.Messages;

[TestFixture, Parallelizable(ParallelScope.All)]
public class SubTreeRangeMessageSerializerTests
{
    public static readonly byte[] Code0 = { 0, 0 };
    public static readonly byte[] Code1 = { 0, 1 };

    [Test]
    public void Roundtrip_NoAccountsNoProofs()
    {
        SubTreeRangeMessage msg = new()
        {
            RequestId = MessageConstants.Random.NextLong(),
            PathsWithSubTrees = Array.Empty<PathWithSubTree>(),
            Proofs = Array.Empty<byte>()
        };

        SubTreeRangeMessageSerializer serializer = new();

        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_Many()
    {
        var acc01 = Build.An.Account
            .WithBalance(1)
            .WithCode(Code0)
            .WithStorageRoot(new Hash256("0x10d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"))
            .TestObject;
        var acc02 = Build.An.Account
            .WithBalance(2)
            .WithCode(Code1)
            .WithStorageRoot(new Hash256("0x20d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"))
            .TestObject;

        SubTreeRangeMessage msg = new()
        {
            RequestId = MessageConstants.Random.NextLong(),
            PathsWithSubTrees = new[] { new PathWithSubTree(TestItem.Stem1, acc01.ToVerkleDict()), new PathWithSubTree(TestItem.Stem2, acc02.ToVerkleDict()) },
            Proofs = TestItem.RandomDataA
        };

        SubTreeRangeMessageSerializer serializer = new();

        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_EmptyStorageRoot()
    {
        var acc01 = Build.An.Account
            .WithBalance(1)
            .WithCode(Code0)
            .WithStorageRoot(Keccak.EmptyTreeHash)
            .TestObject;

        SubTreeRangeMessage msg = new()
        {
            RequestId = MessageConstants.Random.NextLong(),
            PathsWithSubTrees = new[] { new PathWithSubTree(TestItem.Stem2, acc01.ToVerkleDict()) },
            Proofs = TestItem.RandomDataB
        };

        SubTreeRangeMessageSerializer serializer = new();

        SerializerTester.TestZero(serializer, msg);
    }

    [Test]
    public void Roundtrip_EmptyCode()
    {

        var acc01 = Build.An.Account
            .WithBalance(1)
            .WithStorageRoot(TestItem.KeccakA)
            .TestObject;

        SubTreeRangeMessage msg = new()
        {
            RequestId = MessageConstants.Random.NextLong(),
            PathsWithSubTrees = new[] { new PathWithSubTree(TestItem.Stem2, acc01.ToVerkleDict()) },
            Proofs = TestItem.RandomDataA
        };

        SubTreeRangeMessageSerializer serializer = new();

        SerializerTester.TestZero(serializer, msg);
    }
}
