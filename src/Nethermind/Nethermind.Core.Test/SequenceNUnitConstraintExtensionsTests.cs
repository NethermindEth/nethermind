// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class SequenceNUnitConstraintExtensionsTests
{
    private static readonly byte[] Expected = [1, 2, 3];

    private static IEnumerable<TestCaseData> EqualActuals()
    {
        yield return new TestCaseData((object)new byte[] { 1, 2, 3 }).SetName("array");
        yield return new TestCaseData((ReadOnlyMemory<byte>)new byte[] { 1, 2, 3 }).SetName("ReadOnlyMemory");
        yield return new TestCaseData((Memory<byte>)new byte[] { 1, 2, 3 }).SetName("Memory");
        yield return new TestCaseData(new ArraySegment<byte>([0, 1, 2, 3, 4], 1, 3)).SetName("ArraySegment");
        yield return new TestCaseData(new List<byte> { 1, 2, 3 }).SetName("List");
    }

    [TestCaseSource(nameof(EqualActuals))]
    public void Equal_contents_match_whatever_holds_them(object actual)
    {
        Assert.That(actual, Is.SequenceEqualTo(Expected));
        Assert.That(actual, Is.SequenceEqualTo((ReadOnlyMemory<byte>)Expected));
        Assert.That(actual, Is.SequenceEqualTo(new List<byte>(Expected)));
        Assert.That(actual, Is.SequenceEqualTo("\x01\x02\x03"u8));
    }

    [Test]
    public void Span_actual_is_compared_without_a_copy_and_reports_the_differing_index()
    {
        Span<byte> actual = [1, 9, 3];
        Assert.That(actual[..1], Is.SequenceEqualTo([(byte)1]));

        Exception? failure = Assert.Throws<AssertionException>(() => Assert.That(new ReadOnlySpan<byte>([1, 9, 3]), Is.SequenceEqualTo(Expected)));
        Assert.That(failure!.Message, Does.Contain("Values differ at index [1]"));
    }

    [TestCase(new byte[] { 1, 2 }, TestName = "actual shorter")]
    [TestCase(new byte[] { 1, 2, 3, 4 }, TestName = "actual longer")]
    [TestCase(new byte[] { 1, 9, 3 }, TestName = "value differs")]
    public void A_prefix_or_extension_of_the_expected_does_not_match(byte[] actual)
    {
        AssertFails(actual, Is.SequenceEqualTo(Expected));
        AssertFails((ReadOnlyMemory<byte>)actual, Is.SequenceEqualTo(Expected));
        AssertFails(new List<byte>(actual), Is.SequenceEqualTo(Expected));
        AssertFails((ReadOnlyMemory<byte>)actual, Is.SequenceEqualTo(new List<byte>(Expected)));
        AssertFails(new List<byte>(actual), Is.SequenceEqualTo(new List<byte>(Expected)));
        Assert.Throws<AssertionException>(() => Assert.That(new ReadOnlySpan<byte>(actual), Is.SequenceEqualTo(new List<byte>(Expected))));
    }

    [Test]
    public void Memory_mismatch_reports_the_differing_index_not_the_wrapper()
    {
        string message = AssertFails((ReadOnlyMemory<byte>)new byte[] { 1, 9, 3 }, Is.SequenceEqualTo(Expected));

        Assert.That(message, Does.Contain("Values differ at index [1]").And.Not.Contain("ReadOnlyMemory"));
    }

    [Test]
    public void Memory_of_another_element_type_does_not_match() =>
        AssertFails((ReadOnlyMemory<byte>)new byte[] { 1, 2, 3 }, Is.SequenceEqualTo(new int[] { 1, 2, 3 }));

    private static string AssertFails<TActual>(TActual actual, SequenceEqualConstraint<byte> constraint) =>
        Assert.Throws<AssertionException>(() => Assert.That(actual, constraint))!.Message;

    private static void AssertFails<TActual>(TActual actual, SequenceEqualConstraint<int> constraint) =>
        Assert.Throws<AssertionException>(() => Assert.That(actual, constraint));
}
