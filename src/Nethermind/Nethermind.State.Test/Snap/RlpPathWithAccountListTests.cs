// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using NUnit.Framework;

namespace Nethermind.Store.Test.Snap;

[Parallelizable(ParallelScope.All)]
public class RlpPathWithAccountListTests
{
    private static readonly PathWithAccount[] Accounts =
    [
        new(TestItem.KeccakA, Account.TotallyEmpty),
        new(TestItem.KeccakB, new Account(1, 1000)),
        new(TestItem.KeccakC, new Account(2, UInt256.Zero, TestItem.KeccakE, TestItem.KeccakF)),
        new(TestItem.KeccakD, new Account(ulong.MaxValue, UInt256.MaxValue, TestItem.KeccakE, Keccak.OfAnEmptyString)),
    ];

    [Test]
    public void Reads_back_the_encoded_paths_and_accounts([Values] bool fromWire)
    {
        IPathWithAccountList built = Accounts.ToPathWithAccountList();
        using IPathWithAccountList list = fromWire ? ToWireBacked(built) : built;

        Assert.That(list.Count, Is.EqualTo(Accounts.Length));
        for (int i = 0; i < Accounts.Length; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(list.GetPath(i), Is.EqualTo(Accounts[i].Path), $"path {i}");
                Assert.That(list.GetAccount(i).Rlp.ToArray(), Is.EqualTo(AccountDecoder.Slim.EncodeAsBytes(Accounts[i].Account!)), $"slim rlp {i}");
                Assert.That(list.GetAccount(i).ToStruct(), Is.EqualTo(Accounts[i].Account!.ToStruct()), $"account {i}");
            }
        }
    }

    [Test]
    public void Empty_list_is_written_as_an_empty_sequence()
    {
        using IPathWithAccountList built = Array.Empty<PathWithAccount>().ToPathWithAccountList();
        using IPathWithAccountList reparsed = ToWireBacked(EmptyPathWithAccountList.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Write(EmptyPathWithAccountList.Instance), Is.EqualTo(new[] { Rlp.EmptyListByte }));
            Assert.That(Write(built), Is.EqualTo(new[] { Rlp.EmptyListByte }));
            Assert.That(reparsed.Count, Is.Zero);
        }
    }

    private static byte[] Write(IPathWithAccountList list)
    {
        byte[] bytes = new byte[list.RlpLength];
        RlpWriter writer = new(bytes);
        list.Write(ref writer);
        return bytes;
    }

    /// <summary>Re-reads the encoding of <paramref name="list"/>, which it disposes, the way a received message does.</summary>
    private static RlpPathWithAccountList ToWireBacked(IPathWithAccountList list)
    {
        byte[] bytes;
        using (list) bytes = Write(list);
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(bytes.Length);
        bytes.CopyTo(owner.Memory);
        return new RlpPathWithAccountList(new RlpItemList(owner, owner.Memory[..bytes.Length]));
    }
}
