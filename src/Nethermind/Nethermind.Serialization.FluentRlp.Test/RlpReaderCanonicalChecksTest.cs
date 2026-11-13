// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Serialization.FluentRlp.Test;

/// <summary>Tests the reads that skip the checks of the canonical form for trusted input.</summary>
public class RlpReaderCanonicalChecksTest
{
    public enum Read
    {
        Bytes,
        BytesOfLength2,
        IntegerBytes,
        Integer,
        Sequence,
        EncodedItem,
        Count,
    }

    [TestCase("05", Read.Bytes, ExpectedResult = "05@1")]
    [TestCase("80", Read.Bytes, ExpectedResult = "@1")]
    [TestCase("8180", Read.Bytes, ExpectedResult = "80@2")]
    [TestCase("820102", Read.BytesOfLength2, ExpectedResult = "0102@3")]
    [TestCase("820102", Read.IntegerBytes, ExpectedResult = "0102@3")]
    [TestCase("820102", Read.Integer, ExpectedResult = "258@3")]
    [TestCase("c20102", Read.Sequence, ExpectedResult = "0102@3")]
    [TestCase("f838" + Payload56, Read.Sequence, ExpectedResult = Payload56 + "@58")]
    [TestCase("b838" + Payload56, Read.Bytes, ExpectedResult = Payload56 + "@58")]
    [TestCase("c20102", Read.EncodedItem, ExpectedResult = "c20102@3")]
    [TestCase("0102c0", Read.Count, ExpectedResult = "3@0")]
    public string Canonical_input_reads_the_same_in_both_modes(string hex, Read read)
    {
        string checkedResult = Run<OnFlag>(hex, read);
        Assert.That(Run<OffFlag>(hex, read), Is.EqualTo(checkedResult));
        return checkedResult.ToLowerInvariant();
    }

    [TestCase("8105", Read.Bytes, ExpectedResult = "05@2")]
    [TestCase("8105", Read.IntegerBytes, ExpectedResult = "05@2")]
    [TestCase("820005", Read.IntegerBytes, ExpectedResult = "0005@3")]
    [TestCase("820005", Read.Integer, ExpectedResult = "5@3")]
    [TestCase("00", Read.Integer, ExpectedResult = "0@1")]
    [TestCase("b80105", Read.Bytes, ExpectedResult = "05@3")]
    [TestCase("b900020102", Read.Bytes, ExpectedResult = "0102@5")]
    [TestCase("f80105", Read.Sequence, ExpectedResult = "05@3")]
    [TestCase("8105", Read.EncodedItem, ExpectedResult = "8105@2")]
    [TestCase("8105b80105", Read.Count, ExpectedResult = "2@0")]
    public string Non_canonical_input_reads_only_without_checks(string hex, Read read)
    {
        Assert.That(() => Run<OnFlag>(hex, read), Throws.TypeOf<RlpReaderException>());
        return Run<OffFlag>(hex, read).ToLowerInvariant();
    }

    [TestCase("", Read.Bytes)]
    [TestCase("8201", Read.Bytes)]
    [TestCase("b8", Read.Bytes)]
    [TestCase("b838", Read.Bytes)]
    [TestCase("b90100", Read.Bytes)]
    [TestCase("bc01020304", Read.Bytes)]
    [TestCase("c0", Read.Bytes)]
    [TestCase("8201", Read.BytesOfLength2)]
    [TestCase("8301", Read.IntegerBytes)]
    [TestCase("890102030405060708", Read.Integer)]
    [TestCase("c201", Read.Sequence)]
    [TestCase("f8", Read.Sequence)]
    [TestCase("f838", Read.Sequence)]
    [TestCase("80", Read.Sequence)]
    [TestCase("c201", Read.EncodedItem)]
    [TestCase("01c3", Read.Count)]
    public void Truncated_or_malformed_input_throws_in_both_modes(string hex, Read read)
    {
        Assert.That(() => Run<OnFlag>(hex, read), Throws.TypeOf<RlpReaderException>());
        Assert.That(() => Run<OffFlag>(hex, read), Throws.TypeOf<RlpReaderException>());
    }

    private const string Payload56 = "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738";

    private static string Run<TCanonicalChecks>(string hex, Read read) where TCanonicalChecks : struct, IFlag
    {
        RlpReader reader = new(Convert.FromHexString(hex));
        string result;
        switch (read)
        {
            case Read.Bytes:
                result = Convert.ToHexString(reader.ReadBytes<TCanonicalChecks>());
                break;
            case Read.BytesOfLength2:
                result = Convert.ToHexString(reader.ReadBytes<TCanonicalChecks>(2));
                break;
            case Read.IntegerBytes:
                result = Convert.ToHexString(reader.ReadIntegerBytes<TCanonicalChecks>());
                break;
            case Read.Integer:
                result = reader.ReadInteger<ulong, TCanonicalChecks>().ToString();
                break;
            case Read.Sequence:
                RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
                result = Convert.ToHexString(items.ReadRaw(items.Remaining));
                reader.ExitSequence(in items, itemLength);
                break;
            case Read.EncodedItem:
                result = Convert.ToHexString(reader.ReadEncodedItem<TCanonicalChecks>());
                break;
            default:
                result = reader.CountItems<TCanonicalChecks>(10).ToString();
                break;
        }

        return $"{result}@{reader.BytesRead}";
    }
}
