// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtAccountTests
{
    [TestCase("", 32)]
    [TestCase("6001600055", 64)]
    [TestCase("ef010000000000000000000000000000000000000000aa", 55)]
    public void Round_trips_the_stem_encoding(string codeHex, int encodedLength)
    {
        byte[] code = Bytes.FromHexString(codeHex);
        Account account = new Account(7, 9).WithChangedCodeHash(Keccak.Compute(code));
        PbtAccount stem = PbtAccount.From(account, code.Length == 0 ? null : new CodeInfo(code));
        byte[] encoded = new byte[stem.EncodedLength];
        stem.Encode(encoded);
        PbtAccount decoded = PbtAccount.Decode(encoded);
        Account rebalanced = account.WithChangedBalance(10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Has.Length.EqualTo(encodedLength));
            Assert.That(decoded, Is.EqualTo(stem));
            Assert.That(decoded.CodeSize, Is.EqualTo((uint)code.Length));
            Assert.That(decoded.CodeHash, Is.EqualTo(account.CodeHash.ValueHash256));
            Assert.That(decoded.ToAccount(), Is.EqualTo(account));
            Assert.That(PbtAccount.From(decoded, rebalanced).ToAccount(), Is.EqualTo(rebalanced));
        }
    }

    [TestCase("")]
    [TestCase("000000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("0001000000000000000000000000000000000000000000000000000000000000")]
    public void Rejects_a_malformed_encoding(string hex) =>
        Assert.That(() => PbtAccount.Decode(Bytes.FromHexString(hex)), Throws.TypeOf<InvalidDataException>());
}
