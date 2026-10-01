// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class OverrideCodeJsonConverterTests
{
    [Test]
    public void The_same_text_gets_one_shared_array()
    {
        OverrideCodeInterner interner = new();
        OverrideCodeJsonConverter converter = new(interner);
        string text = UniqueCodeText(100);
        byte[] expected = Bytes.FromHexString(text);

        byte[]?[] reads = Enumerable.Range(0, 4).Select(_ => Read(converter, $"\"{text}\"")).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reads, Has.All.EqualTo(expected));
            // The first read only marks the text as seen; the second adds its array, which later reads get.
            Assert.That(reads[1], Is.Not.SameAs(reads[0]));
            Assert.That(reads[2], Is.SameAs(reads[1]));
            Assert.That(reads[3], Is.SameAs(reads[1]));
            Assert.That(interner.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void Account_override_code_is_interned_through_its_attribute()
    {
        string text = UniqueCodeText(200);
        string json = $$"""{"balance":"0x1","code":"{{text}}"}""";

        byte[]?[] reads = Enumerable.Range(0, 5)
            .Select(_ => JsonSerializer.Deserialize<AccountOverride>(json, EthereumJsonSerializer.JsonRpcRequestOptions)!.Code)
            .ToArray();

        Assert.That(reads[4], Is.SameAs(reads[3]));
        Assert.That(reads[4], Is.EqualTo(Bytes.FromHexString(text)));
    }

    [Test]
    public void Account_override_code_is_interned_through_the_generated_rpc_metadata()
    {
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonRpcRequestOptions)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(EthRpcJsonContext.Default, EthereumJsonSerializer.JsonRpcRequestOptions.TypeInfoResolver)
        };
        string text = UniqueCodeText(300);
        string json = $$"""{"code":"{{text}}"}""";

        JsonTypeInfo typeInfo = options.GetTypeInfo(typeof(AccountOverride));
        Assert.That(typeInfo.OriginatingResolver, Is.SameAs(EthRpcJsonContext.Default));
        byte[]?[] reads = Enumerable.Range(0, 5).Select(_ => ((AccountOverride)JsonSerializer.Deserialize(json, typeInfo)!).Code).ToArray();

        Assert.That(reads[4], Is.SameAs(reads[3]));
        Assert.That(reads[4], Is.EqualTo(Bytes.FromHexString(text)));
    }

    [Test]
    public void Different_text_never_shares_an_array_even_with_the_same_hash()
    {
        OverrideCodeInterner interner = new();
        byte[] text = Encoding.ASCII.GetBytes("0x6001600201ab");
        byte[] code = Bytes.FromHexString("0x6001600201ab");
        const int sameHash = 77;

        interner.Add(text, sameHash, code);
        interner.Add(text, sameHash, code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(interner.Find(text, sameHash), Is.SameAs(code));
            // One character apart, and the same bytes in other letter case.
            Assert.That(interner.Find(Encoding.ASCII.GetBytes("0x6001600201ac"), sameHash), Is.Null);
            Assert.That(interner.Find(Encoding.ASCII.GetBytes("0x6001600201AB"), sameHash), Is.Null);
            Assert.That(interner.Find(Encoding.ASCII.GetBytes("0x6001600201ab00"), sameHash), Is.Null);
        }
    }

    [Test]
    public void Texts_of_the_same_bytes_in_other_case_get_their_own_arrays()
    {
        OverrideCodeJsonConverter converter = new(new OverrideCodeInterner());
        string lower = UniqueCodeText(64).ToLowerInvariant();
        string upper = "0x" + lower[2..].ToUpperInvariant();

        byte[]? lowerCode = null, upperCode = null;
        for (int i = 0; i < 3; i++)
        {
            lowerCode = Read(converter, $"\"{lower}\"");
            upperCode = Read(converter, $"\"{upper}\"");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(upperCode, Is.EqualTo(lowerCode));
            Assert.That(upperCode, Is.Not.SameAs(lowerCode));
        }
    }

    [Test]
    public void Text_seen_once_is_not_copied()
    {
        OverrideCodeInterner interner = new();
        OverrideCodeJsonConverter converter = new(interner);

        for (int i = 0; i < 500; i++) Read(converter, $"\"{UniqueCodeText(48)}\"");

        Assert.That(interner.Count, Is.Zero);
    }

    [TestCase(OverrideCodeInterner.MaxCodeLength, true)]
    [TestCase(OverrideCodeInterner.MaxCodeLength + 1, false)]
    public void Code_longer_than_the_cap_is_decoded_on_every_read(int codeLength, bool interned)
    {
        OverrideCodeInterner interner = new();
        OverrideCodeJsonConverter converter = new(interner);
        string text = UniqueCodeText(codeLength);

        byte[]? second = null, third = null;
        for (int i = 0; i < 3; i++)
        {
            second = third;
            third = Read(converter, $"\"{text}\"");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(third, Has.Length.EqualTo(codeLength));
            Assert.That(ReferenceEquals(second, third), Is.EqualTo(interned));
            Assert.That(interner.Count, Is.EqualTo(interned ? 1 : 0));
        }
    }

    [Test]
    public void The_interner_never_holds_more_than_its_capacity()
    {
        OverrideCodeInterner interner = new();
        int capacity = OverrideCodeInterner.Sets * OverrideCodeInterner.Ways;
        for (int i = 0; i < 4 * capacity; i++)
        {
            byte[] text = Encoding.ASCII.GetBytes(UniqueCodeText(40));
            // Hashes that fill every set and the first offer that marks the text as seen.
            interner.Add(text, i, []);
            interner.Add(text, i, []);
        }

        Assert.That(interner.Count, Is.EqualTo(capacity));
    }

    [Test]
    public void Texts_that_share_a_seen_set_are_added_when_they_alternate()
    {
        OverrideCodeInterner interner = new();
        // As many texts as a seen set holds, in one seen set, each in its own set of the table.
        byte[][] texts = Enumerable.Range(0, 4).Select(static _ => Encoding.ASCII.GetBytes(UniqueCodeText(40))).ToArray();
        int[] hashes = texts.Select(static (_, i) => Hash(tableSet: i, seenSet: 9, unique: i)).ToArray();

        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < texts.Length; i++) interner.Add(texts[i], hashes[i], [(byte)i]);
        }

        for (int i = 0; i < texts.Length; i++) Assert.That(interner.Find(texts[i], hashes[i]), Is.Not.Null, $"text {i}");
    }

    [Test]
    public void Texts_in_use_stay_while_new_texts_keep_coming_back_to_their_set()
    {
        OverrideCodeInterner interner = new();
        const int set = 5;
        byte[][] hot = Enumerable.Range(0, OverrideCodeInterner.Ways).Select(static _ => Encoding.ASCII.GetBytes(UniqueCodeText(40))).ToArray();
        int[] hotHashes = hot.Select(static (_, i) => Hash(set, seenSet: i, unique: i)).ToArray();
        for (int i = 0; i < hot.Length; i++) OfferTwice(interner, hot[i], hotHashes[i]);

        for (int request = 0; request < 50 * interner.RefusalsBeforeAging; request++)
        {
            for (int i = 0; i < hot.Length; i++) Assert.That(interner.Find(hot[i], hotHashes[i]), Is.Not.Null, $"text {i} at request {request}");

            // A text the set has no room for, sent twice so each would be added if room were made by dropping one.
            OfferTwice(interner, Encoding.ASCII.GetBytes(UniqueCodeText(40)), Hash(set, seenSet: 100 + request % 100, unique: 100 + request));
        }

        Assert.That(interner.Count, Is.EqualTo(OverrideCodeInterner.Ways));
    }

    [Test]
    public void Texts_no_longer_found_make_way_once_their_set_ages()
    {
        OverrideCodeInterner interner = new();
        const int set = 6;
        for (int i = 0; i < OverrideCodeInterner.Ways; i++) OfferTwice(interner, Encoding.ASCII.GetBytes(UniqueCodeText(40)), Hash(set, seenSet: i, unique: i));

        // The four texts are never found again; a new one keeps coming back.
        byte[] text = Encoding.ASCII.GetBytes(UniqueCodeText(40));
        int hash = Hash(set, seenSet: 50, unique: 50);
        for (int attempt = 1; attempt <= interner.RefusalsBeforeAging; attempt++)
        {
            OfferTwice(interner, text, hash);
            Assert.That(interner.Find(text, hash), Is.Null, $"attempt {attempt}, before the set ages");
        }

        OfferTwice(interner, text, hash);
        Assert.That(interner.Find(text, hash), Is.Not.Null, "the attempt after the set aged");
    }

    [Test]
    public void A_full_set_drops_its_oldest_text_not_found_since_it_aged()
    {
        OverrideCodeInterner interner = new();
        const int set = 8;
        byte[][] texts = Enumerable.Range(0, OverrideCodeInterner.Ways).Select(static _ => Encoding.ASCII.GetBytes(UniqueCodeText(40))).ToArray();
        int[] hashes = texts.Select(static (_, i) => Hash(set, seenSet: i, unique: i)).ToArray();
        for (int i = 0; i < texts.Length; i++) OfferTwice(interner, texts[i], hashes[i]);

        for (int i = 0; i < interner.RefusalsBeforeAging; i++)
            OfferTwice(interner, Encoding.ASCII.GetBytes(UniqueCodeText(40)), Hash(set, seenSet: 10 + i, unique: 10 + i));

        // After the set aged, the oldest text is found again, so a new one drops the second oldest instead.
        Assert.That(interner.Find(texts[0], hashes[0]), Is.Not.Null);
        OfferTwice(interner, Encoding.ASCII.GetBytes(UniqueCodeText(40)), Hash(set, seenSet: 200, unique: 200));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(interner.Find(texts[0], hashes[0]), Is.Not.Null, "the oldest, found since the set aged");
            Assert.That(interner.Find(texts[1], hashes[1]), Is.Null, "the second oldest");
            for (int i = 2; i < texts.Length; i++) Assert.That(interner.Find(texts[i], hashes[i]), Is.Not.Null, $"text {i}");
        }
    }

    [Test]
    public void A_text_that_fell_out_is_added_again_only_once_it_is_offered_twice()
    {
        OverrideCodeInterner interner = new();
        const int set = 7;
        byte[] first = Encoding.ASCII.GetBytes(UniqueCodeText(40));
        int firstHash = Hash(set, seenSet: 1, unique: 1);
        OfferTwice(interner, first, firstHash);
        for (int i = 2; i <= OverrideCodeInterner.Ways; i++) OfferTwice(interner, Encoding.ASCII.GetBytes(UniqueCodeText(40)), Hash(set, seenSet: i, unique: i));

        // Age the set, then add one more text, which drops the oldest: the first.
        for (int i = 0; i < interner.RefusalsBeforeAging; i++)
            OfferTwice(interner, Encoding.ASCII.GetBytes(UniqueCodeText(40)), Hash(set, seenSet: 10 + i, unique: 10 + i));
        OfferTwice(interner, Encoding.ASCII.GetBytes(UniqueCodeText(40)), Hash(set, seenSet: 200, unique: 200));
        Assert.That(interner.Find(first, firstHash), Is.Null, "dropped");

        interner.Add(first, firstHash, []);
        Assert.That(interner.Find(first, firstHash), Is.Null, "offered once after it was dropped");

        interner.Add(first, firstHash, []);
        Assert.That(interner.Find(first, firstHash), Is.Not.Null, "offered twice after it was dropped");
    }

    [Test]
    public void A_long_text_changed_in_any_sample_or_in_length_gets_another_hash()
    {
        // Head and tail are random, the middle repeats "60", so a text two code bytes longer has the same samples.
        string head = UniqueCodeText(256), tail = UniqueCodeText(256)[2..];
        string Text(int middleBytes) => head + string.Concat(Enumerable.Repeat("60", middleBytes)) + tail;
        string text = Text(5_000);
        int hash = HashOf(text);

        static string ChangedAt(string text, int index) => text[..index] + (text[index] == 'a' ? 'b' : 'a') + text[(index + 1)..];
        int middle = (text.Length - OverrideCodeInterner.SampleLength) / 2 + 7;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(HashOf(ChangedAt(text, 10)), Is.Not.EqualTo(hash), "first sample");
            Assert.That(HashOf(ChangedAt(text, middle)), Is.Not.EqualTo(hash), "middle sample");
            Assert.That(HashOf(ChangedAt(text, text.Length - 3)), Is.Not.EqualTo(hash), "last sample");
            Assert.That(HashOf(Text(5_002)), Is.Not.EqualTo(hash), "length");
            Assert.That(HashOf(text), Is.EqualTo(hash), "the same text");
        }
    }

    [Test]
    public void Long_texts_that_differ_only_outside_the_samples_get_their_own_arrays()
    {
        OverrideCodeJsonConverter converter = new(new OverrideCodeInterner());
        string text = UniqueCodeText(5_000);
        string other = text[..1_000] + (text[1_000] == 'a' ? 'b' : 'a') + text[1_001..];
        Assert.That(HashOf(other), Is.EqualTo(HashOf(text)));

        byte[]? textCode = null, otherCode = null;
        for (int i = 0; i < 3; i++)
        {
            textCode = Read(converter, $"\"{text}\"");
            otherCode = Read(converter, $"\"{other}\"");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(textCode, Is.EqualTo(Bytes.FromHexString(text)));
            Assert.That(otherCode, Is.EqualTo(Bytes.FromHexString(other)));
            Assert.That(Read(converter, $"\"{text}\""), Is.SameAs(textCode));
            Assert.That(Read(converter, $"\"{other}\""), Is.SameAs(otherCode));
        }
    }

    [Test]
    public void Concurrent_reads_get_the_bytes_of_their_own_text()
    {
        OverrideCodeJsonConverter converter = new(new OverrideCodeInterner());
        string[] texts = Enumerable.Range(0, 16).Select(static i => UniqueCodeText(64 + i)).ToArray();
        byte[][] codes = texts.Select(static text => Bytes.FromHexString(text)).ToArray();

        Parallel.For(0, 20_000, i =>
        {
            int k = i % texts.Length;
            if (!Read(converter, $"\"{texts[k]}\"").AsSpan().SequenceEqual(codes[k]))
                throw new InvalidOperationException($"wrong bytes for text {k}");
        });
    }

    // The value, and what it reads as: its bytes, "null", or null for an exception.
    private static IEnumerable<TestCaseData> UnusualCodeValues()
    {
        yield return new TestCaseData("\"0x123\"", "0x0123").SetName("odd length");
        yield return new TestCaseData("\"6001600201\"", "0x6001600201").SetName("no prefix");
        yield return new TestCaseData("\"0x6001600201\"", "0x6001600201").SetName("plain");
        yield return new TestCaseData("\"0x\"", "0x").SetName("prefix only");
        yield return new TestCaseData("\"\"", "null").SetName("empty string");
        yield return new TestCaseData("null", "null").SetName("null");
        yield return new TestCaseData("\"0x60016002zz\"", null).SetName("not hex");
        yield return new TestCaseData("\"0x6001600g\"", null).SetName("odd length, not hex");
        yield return new TestCaseData(@"""0x\u0036001600201""", null).SetName("escaped");
        yield return new TestCaseData("\"0X6001600201\"", null).SetName("upper-case prefix");
        yield return new TestCaseData("1234", null).SetName("number");
        yield return new TestCaseData("true", null).SetName("boolean");
        yield return new TestCaseData("{}", null).SetName("object");
        yield return new TestCaseData("[]", null).SetName("array");
    }

    [TestCaseSource(nameof(UnusualCodeValues))]
    public void Any_code_value_reads_exactly_as_with_the_plain_byte_array_converter(string value, string? decoded)
    {
        string json = $$"""{"code":{{value}}}""";
        string expected = Outcome(static json => JsonSerializer.Deserialize<PlainAccountOverride>(json, EthereumJsonSerializer.JsonRpcRequestOptions)?.Code, json);
        Assert.That(expected, decoded is null ? Does.Contain("Exception: ") : Is.EqualTo(decoded), "the plain converter");

        // Repeated, so a text that decodes is also read once it is interned.
        for (int i = 0; i < 3; i++)
        {
            string actual = Outcome(static json => JsonSerializer.Deserialize<AccountOverride>(json, EthereumJsonSerializer.JsonRpcRequestOptions)?.Code, json);
            Assert.That(actual, Is.EqualTo(expected), $"read {i}");
        }
    }

    [Test]
    public void Code_is_written_as_before()
    {
        byte[] code = Bytes.FromHexString("0x00600160020100");

        string written = JsonSerializer.Serialize(new AccountOverride { Code = code }, EthereumJsonSerializer.JsonOptions);
        string plain = JsonSerializer.Serialize(new PlainAccountOverride { Code = code }, EthereumJsonSerializer.JsonOptions);

        using JsonDocument writtenDocument = JsonDocument.Parse(written);
        using JsonDocument plainDocument = JsonDocument.Parse(plain);
        Assert.That(writtenDocument.RootElement.GetProperty("code").GetRawText(), Is.EqualTo(plainDocument.RootElement.GetProperty("code").GetRawText()));
    }

    private sealed class PlainAccountOverride
    {
        public byte[]? Code { get; set; }
    }

    private static string Outcome(Func<string, byte[]?> read, string json)
    {
        try
        {
            byte[]? code = read(json);
            return code is null ? "null" : code.ToHexString(true);
        }
        catch (Exception e)
        {
            return $"{e.GetType().FullName}: {e.Message}";
        }
    }

    private static byte[]? Read(OverrideCodeJsonConverter converter, string json)
    {
        Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(json));
        reader.Read();
        return converter.Read(ref reader, typeof(byte[]), EthereumJsonSerializer.JsonRpcRequestOptions);
    }

    private static int HashOf(string text) => OverrideCodeInterner.HashOf(Encoding.ASCII.GetBytes(text));

    // A hash that picks the given set of the table and of the seen filter.
    private static int Hash(int tableSet, int seenSet, int unique) => (seenSet << 24) | (unique << 6) | tableSet;

    private static void OfferTwice(OverrideCodeInterner interner, byte[] text, int hash)
    {
        interner.Add(text, hash, []);
        interner.Add(text, hash, []);
    }

    private static string UniqueCodeText(int length)
    {
        byte[] code = new byte[length];
        Random.Shared.NextBytes(code);
        return code.ToHexString(true);
    }
}
