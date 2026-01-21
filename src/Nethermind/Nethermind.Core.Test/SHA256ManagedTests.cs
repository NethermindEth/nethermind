// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class SHA256ManagedTests
{
    [TestCase("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [TestCase("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [TestCase("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    [TestCase("abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu", "cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1")]
    public void Should_hash_correctly_basic(string input, string output)
    {
        byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
        byte[] actualHash = SHA256Managed.HashData(inputBytes);

        Assert.That(Convert.ToHexStringLower(actualHash), Is.EqualTo(output));
    }

    [Test]
    public void Should_hash_correctly_1M_a()
    {
        byte[] inputBytes = [.. Enumerable.Repeat<byte>(0x61, 1_000_000)];
        byte[] actualHash = SHA256Managed.HashData(inputBytes);

        Assert.That(Convert.ToHexStringLower(actualHash), Is.EqualTo("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0"));
    }
}
