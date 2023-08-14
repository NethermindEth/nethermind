// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class TrieExceptionTests
{
    [Test]
    public void When_MissingBaseExceptionIsRootHash_Then_MentionItClearly()
    {
        MissingNodeException? baseException = new(TestItem.KeccakA);
        try
        {
            TrieException.ThrowOnLoadFailure(TestItem.KeccakB.Bytes, TestItem.KeccakA, baseException);
            Assert.Fail("Should throw");
        }
        catch (TrieException exception)
        {
            exception.Message.Should().Be($"Failed to load root hash {TestItem.KeccakA} while loading key {TestItem.KeccakB.Bytes.ToHexString()}.");
        }
    }

    [Test]
    public void When_CreateOnLoadFailure_WrapMessage()
    {
        MissingNodeException? baseException = new(TestItem.KeccakA);
        try
        {
            TrieException.ThrowOnLoadFailure(TestItem.KeccakB.Bytes, TestItem.KeccakC, baseException);
            Assert.Fail("Should throw");
        }
        catch (TrieException exception)
        {
            exception.Message.Should().Be($"Failed to load key {TestItem.KeccakB.Bytes.ToHexString()} from root hash {TestItem.KeccakC}.");
        }
    }
}
