// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using LevelDB;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.Migration;

public class LevelDBTest: TestWithLevelDbFix
{
    [TestCase(@"D:\.lighthouse\mainnet 1\beacon\freezer_db")]
    [TestCase(@"D:\Nethermind\xdc\chaindata")]
    public void EnumerateKeys(string dbPath)
    {
        var fileName = $"keys-{Guid.NewGuid():N}.txt";
        using var file = new StreamWriter(fileName);
        var options = new Options { CreateIfMissing = false };
        using var db = new DB(options, dbPath);
        TestContext.Out.WriteLine(fileName);
        foreach (var (key, value) in db)
        {
            file.WriteLine($"{Convert.ToHexString(key)}: {Convert.ToHexString(value)}");
            Assert.That(db.Get(key), Is.EqualTo(value));
        }
    }

    // [TestCase(@"D:\Nethermind\xdc\chaindata", "0x863211afe152783454003874d0e127c1ca7ad92b")]
    // public void AddressBalance(string dbPath, string address)
    // {
    //     var options = new Options { CreateIfMissing = false };
    //     using var db = new DB(options, dbPath);
    //
    //     var lastHeaderRlp = db.Get("LastHeader"u8.ToArray());
    //     Assert.That(lastHeaderRlp, Is.Not.Null.And.Not.Empty);
    //
    //     BlockHeader lastHeader = new XdcHeaderDecoder().Decode(lastHeaderRlp);
    //     Hash256 stateRoot = lastHeader.StateRoot!;
    //
    //     byte[]? rootNodeRlp = db.Get(stateRoot.BytesToArray());
    //     Assert.That(rootNodeRlp, Is.Not.Null.And.Not.Empty, "State root not found");
    //
    //     Hash256 addressKey = Keccak.Compute(new Address(address).Bytes);
    //     var addressRlp = db.Get(addressKey.BytesToArray());
    //
    //     Assert.That(addressRlp, Is.Not.Null.And.Not.Empty);
    //
    //     var decoder = new AccountDecoder();
    //     Account account = decoder.Decode(addressRlp)!;
    //
    //     Assert.That(account.Balance.IsZero, Is.False);
    // }
}
