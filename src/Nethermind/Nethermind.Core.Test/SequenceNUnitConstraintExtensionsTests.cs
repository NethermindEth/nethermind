// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
        yield return new TestCaseData(Expected.Select(static b => b)).SetName("lazy sequence");
    }

    [TestCaseSource(nameof(EqualActuals))]
    public void Equal_contents_match_whatever_holds_them(object actual)
    {
        Assert.That(actual, Is.SequenceEqualTo(Expected));
        Assert.That(actual, Is.SequenceEqualTo((ReadOnlyMemory<byte>)Expected));
        Assert.That(actual, Is.SequenceEqualTo((Memory<byte>)Expected));
        Assert.That(actual, Is.SequenceEqualTo(new List<byte>(Expected)));
        Assert.That(actual, Is.SequenceEqualTo(Expected.Select(static b => b)));
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
        AssertFails(actual, Is.SequenceEqualTo(Expected.Select(static b => b)));
        AssertFails(actual.Select(static b => b), Is.SequenceEqualTo(Expected));
        AssertFails(actual.Select(static b => b), Is.SequenceEqualTo(Expected.Select(static b => b)));
        Assert.Throws<AssertionException>(() => Assert.That(new ReadOnlySpan<byte>(actual), Is.SequenceEqualTo(Expected.Select(static b => b))));
    }

    [Test]
    public void Memory_mismatch_reports_the_differing_index_not_the_wrapper()
    {
        string message = AssertFails((ReadOnlyMemory<byte>)new byte[] { 1, 9, 3 }, Is.SequenceEqualTo(Expected));

        Assert.That(message, Does.Contain("Values differ at index [1]").And.Not.Contain("ReadOnlyMemory"));
    }

    [Test]
    public void Lazy_sequence_mismatch_reports_the_differing_index_from_what_was_read()
    {
        Assert.That(AssertFails(new byte[] { 1, 9, 3 }, Is.SequenceEqualTo(Expected.Select(static b => b))), Does.Contain("Values differ at index [1]"));
        Assert.That(AssertFails(new byte[] { 1, 9, 3 }.Select(static b => b), Is.SequenceEqualTo(Expected)), Does.Contain("Values differ at index [1]"));
    }

    [Test]
    public void Passing_assert_copies_neither_the_actual_nor_the_expected()
    {
        byte[] buffer = new byte[2 * 1024 * 1024];
        ReadOnlyMemory<byte> expected = buffer.AsMemory(1, 1024 * 1024);
        ReadOnlyMemory<byte> actual = buffer.AsMemory(1, 1024 * 1024);
        Assert.That(actual, Is.SequenceEqualTo(expected));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.That(actual, Is.SequenceEqualTo(expected));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.LessThan(64 * 1024));
    }

    [Test]
    public void Null_expected_is_rejected_rather_than_matching_an_empty_actual()
    {
        Assert.Throws<ArgumentNullException>(() => Is.SequenceEqualTo((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => Is.SequenceEqualTo((IEnumerable<byte>)null!));
    }

    [Test]
    public void Expected_sequence_is_read_once_so_a_mismatch_cannot_turn_into_a_pass() =>
        AssertFails((ReadOnlyMemory<byte>)new byte[] { 1 }, Is.SequenceEqualTo(TwoOnFirstReadThenOne()));

    [Test]
    public void Actual_sequence_is_read_once_so_a_mismatch_cannot_turn_into_a_pass() =>
        AssertFails(TwoOnFirstReadThenOne(), Is.SequenceEqualTo(new byte[] { 1 }));

    [Test]
    public void Nested_sequences_compare_by_contents_even_when_their_Equals_says_otherwise()
    {
        AssertFails(new[] { new IdEqualList(1, 1) }, Is.SequenceEqualTo(new[] { new IdEqualList(1, 2) }));
        AssertFails(new object[] { new IdEqualList(1, 1) }, Is.SequenceEqualTo(new object[] { new IdEqualList(1, 2) }));
    }

    [Test]
    public void Actual_container_equality_is_ignored_like_ToArray_would() =>
        AssertFails(new LengthEqualList(2), Is.SequenceEqualTo(new byte[] { 1 }));

    [Test]
    public void Default_array_segment_does_not_match_an_empty_expected() =>
        Assert.That(() => Assert.That(default(ArraySegment<byte>), Is.SequenceEqualTo(Array.Empty<byte>())), Throws.Exception);

    [Test]
    public void Memory_of_another_element_type_does_not_match() =>
        AssertFails((ReadOnlyMemory<byte>)new byte[] { 1, 2, 3 }, Is.SequenceEqualTo(new int[] { 1, 2, 3 }));

    private static string AssertFails<TActual, T>(TActual actual, SequenceEqualConstraint<T> constraint) =>
        Assert.Throws<AssertionException>(() => Assert.That(actual, constraint))!.Message;

    private static IEnumerable<byte> TwoOnFirstReadThenOne()
    {
        int reads = 0;
        return Read();

        IEnumerable<byte> Read()
        {
            yield return reads++ == 0 ? (byte)2 : (byte)1;
        }
    }

    private sealed class LengthEqualList(params byte[] items) : List<byte>(items), IEquatable<byte[]>
    {
        public bool Equals(byte[]? other) => other?.Length == Count;
    }

    private sealed class IdEqualList(int id, params int[] items) : List<int>(items)
    {
        public override bool Equals(object? obj) => obj is IdEqualList other && other._id == _id;
        public override int GetHashCode() => _id;

        private readonly int _id = id;
    }
}
