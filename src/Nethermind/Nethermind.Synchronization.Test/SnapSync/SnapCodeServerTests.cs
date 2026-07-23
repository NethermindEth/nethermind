// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.SnapServer;
using Nethermind.Synchronization.SnapServer;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapCodeServerTests
{
    private MemDb _codeDb = null!;
    private SnapCodeServer _server = null!;

    [SetUp]
    public void SetUp()
    {
        _codeDb = new MemDb();
        _server = new SnapCodeServer(_codeDb);
    }

    [TearDown]
    public void TearDown() => _codeDb.Dispose();

    private ValueHash256 StoreCode(byte[] code)
    {
        Hash256 hash = Keccak.Compute(code);
        _codeDb[hash.Bytes] = code;
        return hash.ValueHash256;
    }

    [Test]
    public void GetByteCodes_returns_requested_codes_in_order()
    {
        byte[] codeA = [1, 2, 3];
        byte[] codeB = [4, 5, 6, 7];
        ValueHash256 hashA = StoreCode(codeA);
        ValueHash256 hashB = StoreCode(codeB);

        using IByteArrayList result = _server.GetByteCodes([hashA, hashB], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0].ToArray(), Is.EqualTo(codeA));
        Assert.That(result[1].ToArray(), Is.EqualTo(codeB));
    }

    [Test]
    public void GetByteCodes_empty_string_hash_returns_empty_entry()
    {
        using IByteArrayList result = _server.GetByteCodes(
            [Keccak.OfAnEmptyString.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Length, Is.EqualTo(0));
    }

    [Test]
    public void GetByteCodes_skips_missing_code()
    {
        byte[] code = [1, 2, 3];
        ValueHash256 present = StoreCode(code);
        ValueHash256 missing = Keccak.Compute([9, 9, 9]).ValueHash256;

        using IByteArrayList result = _server.GetByteCodes([missing, present], long.MaxValue, CancellationToken.None);

        // The missing hash contributes no entry, so only the present code is returned.
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].ToArray(), Is.EqualTo(code));
    }

    [Test]
    public void GetByteCodes_respects_byte_limit()
    {
        ValueHash256 hashA = StoreCode(new byte[100]);
        ValueHash256 hashB = StoreCode(new byte[100]);

        // A byte limit smaller than the first code stops the loop after a single entry is written.
        using IByteArrayList result = _server.GetByteCodes([hashA, hashB], 1, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(1));
    }
}
