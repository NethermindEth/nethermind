// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Filters;

[Parallelizable(ParallelScope.All)]
public class AnyTopicsFilterTests
{
    // Stored without their leading zeros in compact receipts, so they are read back through the iterator's buffer.
    // The longer one is read first in its case, so a buffer left dirty would corrupt the shorter one.
    private static readonly Hash256 Short = WithLeadingZeros(0x04);
    private static readonly Hash256 Longer = WithLeadingZeros(0x01, 0x02, 0x03);

    private static IEnumerable<TestCaseData> Cases()
    {
        yield return Case("[A] in [B, A]", [Specific(TestItem.KeccakA)], [TestItem.KeccakB, TestItem.KeccakA], true);
        yield return Case("[B, A] in [A]", [Specific(TestItem.KeccakB), Specific(TestItem.KeccakA)], [TestItem.KeccakA], true);
        yield return Case("[A, B] in [B, A]", [Specific(TestItem.KeccakA), Specific(TestItem.KeccakB)], [TestItem.KeccakB, TestItem.KeccakA], true);
        yield return Case("[A or C] in [B, C]", [new OrExpression(Specific(TestItem.KeccakA), Specific(TestItem.KeccakC))], [TestItem.KeccakB, TestItem.KeccakC], true);
        yield return Case("[zero] in [B, zero]", [Specific(Keccak.Zero)], [TestItem.KeccakB, Keccak.Zero], true);
        yield return Case("[short] in [longer, short]", [Specific(Short)], [Longer, Short], true);
        yield return Case("[A] in [short, A]", [Specific(TestItem.KeccakA)], [Short, TestItem.KeccakA], true);
        yield return Case("[any] in [B]", [AnyTopic.Instance], [TestItem.KeccakB], true);
        yield return Case("[A] in [B, C]", [Specific(TestItem.KeccakA)], [TestItem.KeccakB, TestItem.KeccakC], false);
        yield return Case("[] in [A]", [], [TestItem.KeccakA], false);
        yield return Case("[any] in []", [AnyTopic.Instance], [], false);

        static TestCaseData Case(string name, TopicExpression[] expressions, Hash256[] topics, bool expected) =>
            new TestCaseData(expressions, topics, expected).SetArgDisplayNames(name);
    }

    [TestCaseSource(nameof(Cases))]
    public void Accepts_a_log_when_any_expression_accepts_any_of_its_topics(TopicExpression[] expressions, Hash256[] topics, bool expected)
    {
        AnyTopicsFilter filter = new(expressions);
        LogEntry entry = new(TestItem.AddressA, [1, 2, 3], topics);
        LogEntryStructRef materialised = new(entry);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.Accepts(entry), Is.EqualTo(expected), "log entry");
            Assert.That(filter.Accepts(ref materialised), Is.EqualTo(expected), "struct over the log entry's topics");
            Assert.That(AcceptsStored(filter, entry, compact: false), Is.EqualTo(expected), "stored receipt");
            Assert.That(AcceptsStored(filter, entry, compact: true), Is.EqualTo(expected), "compact stored receipt");
        }
    }

    // The decoders the receipt iterator uses, which leave the topics as RLP for the filter to walk.
    private static bool AcceptsStored(AnyTopicsFilter filter, LogEntry entry, bool compact)
    {
        if (compact)
        {
            RlpReader compactReader = new(CompactLogEntryDecoder.Instance.Encode(entry));
            CompactLogEntryDecoder.DecodeLogEntryStructRef(ref compactReader, RlpBehaviors.None, out LogEntryStructRef compactLog);
            return filter.Accepts(ref compactLog);
        }

        RlpReader reader = new(LogEntryDecoder.Instance.Encode(entry));
        LogEntryDecoder.DecodeStructRef(ref reader, RlpBehaviors.None, out LogEntryStructRef log);
        return filter.Accepts(ref log);
    }

    private static TopicExpression Specific(Hash256 topic) => new SpecificTopic(topic);

    private static Hash256 WithLeadingZeros(params byte[] suffix)
    {
        byte[] bytes = new byte[Hash256.Size];
        suffix.CopyTo(bytes, Hash256.Size - suffix.Length);
        return new Hash256(bytes);
    }
}
