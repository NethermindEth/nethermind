// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Mcp.Plugin.Tools;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class McpToolInputTests
{
    private const string Hash = "0x1111111111111111111111111111111111111111111111111111111111111111";

    [TestCase("latest", BlockParameterType.Latest)]
    [TestCase("LATEST", BlockParameterType.Latest)]
    [TestCase("earliest", BlockParameterType.Earliest)]
    [TestCase("safe", BlockParameterType.Safe)]
    [TestCase("finalized", BlockParameterType.Finalized)]
    [TestCase("0x10", BlockParameterType.BlockNumber)]
    [TestCase("0X10", BlockParameterType.BlockNumber)]
    [TestCase("16", BlockParameterType.BlockNumber)]
    [TestCase(Hash, BlockParameterType.BlockHash)]
    public void Block_selector_is_parsed(string input, BlockParameterType expected)
    {
        bool parsed = McpToolInput.TryParseBlock(input, "block", out BlockParameter? block, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True, error);
            Assert.That(block?.Type, Is.EqualTo(expected));
            if (expected == BlockParameterType.BlockNumber) Assert.That(block?.BlockNumber, Is.EqualTo(16UL));
            if (expected == BlockParameterType.BlockHash) Assert.That(block?.BlockHash, Is.EqualTo(new Hash256(Hash)));
        }
    }

    [Test]
    public void Uppercase_hex_prefix_is_accepted()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(McpToolInput.TryParseAddress("0X" + new string('A', 40), "address", out Address? address, out _), Is.True);
            Assert.That(address, Is.EqualTo(new Address("0x" + new string('a', 40))));
            Assert.That(McpToolInput.TryParseHash("0X" + Hash[2..], "hash", out Hash256? hash, out _), Is.True);
            Assert.That(hash, Is.EqualTo(new Hash256(Hash)));
            Assert.That(McpToolInput.TryParseData("0XABCD", "data", 8, out byte[]? data, out _), Is.True);
            Assert.That(data, Is.EqualTo(new byte[] { 0xab, 0xcd }));
            Assert.That(McpToolInput.TryParseUInt256("0XFF", "value", out UInt256 value, out _), Is.True);
            Assert.That(value, Is.EqualTo((UInt256)255));
        }
    }

    [TestCase("pending", "pending")]
    [TestCase(null, "block")]
    [TestCase("", "block")]
    [TestCase("-1", "block")]
    [TestCase("0x", "block")]
    [TestCase("0xg", "block")]
    [TestCase("0x10000000000000000", "block")] // 2^64
    [TestCase("18446744073709551616", "block")] // 2^64
    [TestCase("+1", "block")]
    [TestCase(" 1", "block")]
    [TestCase("1_000", "block")]
    public void Invalid_block_selector_names_the_parameter(string? input, string expectedInMessage)
    {
        bool parsed = McpToolInput.TryParseBlock(input, "block", out _, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.False);
            Assert.That(error, Does.Contain(expectedInMessage));
        }
    }

    [TestCase("0x0", "0")]
    [TestCase("0x00", "0")]
    [TestCase("0xff", "255")]
    [TestCase("255", "255")]
    [TestCase("0x" + "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "115792089237316195423570985008687907853269984665640564039457584007913129639935")]
    public void UInt256_quantity_is_parsed(string input, string expected)
    {
        bool parsed = McpToolInput.TryParseUInt256(input, "value", out UInt256 value, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True, error);
            Assert.That(value, Is.EqualTo(UInt256.Parse(expected)));
        }
    }

    [TestCase("-1")]
    [TestCase("0x")]
    [TestCase("0x1" + "0000000000000000000000000000000000000000000000000000000000000000")] // 2^256
    [TestCase("115792089237316195423570985008687907853269984665640564039457584007913129639936")] // 2^256
    [TestCase("1.0")]
    [TestCase("")]
    public void Invalid_UInt256_quantity_is_rejected(string input) =>
        Assert.That(McpToolInput.TryParseUInt256(input, "value", out _, out _), Is.False);

    [TestCase("0x", 0, true)]
    [TestCase("0x0102", 2, true)]
    [TestCase("0x0102", 1, false)]
    [TestCase("0x012", 8, false)]
    [TestCase("0102", 8, false)]
    [TestCase("0xzz", 8, false)]
    public void Data_is_parsed_within_limit(string input, int maxBytes, bool expected) =>
        Assert.That(McpToolInput.TryParseData(input, "data", maxBytes, out _, out _), Is.EqualTo(expected));

    [Test]
    public void Topics_accept_wildcards_hashes_and_alternatives()
    {
        JsonElement[] topics = JsonSerializer.Deserialize<JsonElement[]>($$"""[null, "{{Hash}}", ["{{Hash}}", "{{Hash}}"], []]""")!;

        bool parsed = McpToolInput.TryParseTopics(topics, "topics", out Hash256[]?[]? filter, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True, error);
            Assert.That(filter![0], Is.Null);
            Assert.That(filter[1], Has.Length.EqualTo(1));
            Assert.That(filter[2], Has.Length.EqualTo(2));
            Assert.That(filter[3], Is.Null, "an empty alternative list matches any topic");
        }
    }

    [TestCase("[1]")]
    [TestCase("[true]")]
    [TestCase("[{}]")]
    [TestCase("[\"0x12\"]")]
    [TestCase("[[1]]")]
    [TestCase("[[null]]")]
    [TestCase("[null, null, null, null, null]")]
    public void Invalid_topics_are_rejected(string json) =>
        Assert.That(McpToolInput.TryParseTopics(JsonSerializer.Deserialize<JsonElement[]>(json), "topics", out _, out _), Is.False);

    [Test]
    public void Address_list_is_bounded()
    {
        string[] addresses = Enumerable.Range(0, McpToolInput.MaxLogAddresses + 1).Select(static i => $"0x{i:x40}").ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(McpToolInput.TryParseAddresses(addresses[..McpToolInput.MaxLogAddresses], "address", out _, out _), Is.True);
            Assert.That(McpToolInput.TryParseAddresses(addresses, "address", out _, out _), Is.False);
        }
    }

    [TestCase("0", "0")]
    [TestCase("0x0", "0")]
    [TestCase("7", "7")]
    [TestCase("0x290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e563", "18569430475105882587588266137607568536673111973893317399460219858819262702947")]
    public void Storage_slot_is_parsed(string input, string expected)
    {
        bool parsed = McpToolInput.TryParseStorageSlot(input, "slot", out UInt256 slot, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True, error);
            Assert.That(slot, Is.EqualTo(UInt256.Parse(expected)));
        }
    }

    [TestCase("")]
    [TestCase("-1")]
    [TestCase("slot")]
    [TestCase("0x1" + "0000000000000000000000000000000000000000000000000000000000000000")]
    public void Invalid_storage_slot_names_the_parameter(string input)
    {
        bool parsed = McpToolInput.TryParseStorageSlot(input, "slot", out _, out string? error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.False);
            Assert.That(error, Does.Contain("'slot'"));
        }
    }

    [Test]
    public void Storage_slot_list_is_bounded_and_names_the_bad_entry()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(McpToolInput.TryParseStorageSlots(null, "keys", 2, out UInt256[]? none, out _), Is.True);
            Assert.That(none, Is.Empty);
            Assert.That(McpToolInput.TryParseStorageSlots(["0", "0x1"], "keys", 2, out UInt256[]? two, out _), Is.True);
            Assert.That(two, Is.EqualTo(new UInt256[] { 0, 1 }));
            Assert.That(McpToolInput.TryParseStorageSlots(["0", "1", "2"], "keys", 2, out _, out _), Is.False);
            Assert.That(McpToolInput.TryParseStorageSlots(["0", "x"], "keys", 2, out _, out string? error), Is.False);
            Assert.That(error, Does.Contain("keys[1]"));
        }
    }

    [TestCase(new[] { 10.0, 50.0, 90.0 }, true)]
    [TestCase(new[] { 0.0, 100.0 }, true)]
    [TestCase(new[] { 50.0, 50.0 }, true)]
    [TestCase(new[] { 90.0, 10.0 }, false)]
    [TestCase(new[] { -1.0 }, false)]
    [TestCase(new[] { 100.5 }, false)]
    [TestCase(new[] { double.NaN }, false)]
    [TestCase(new double[0], false)]
    [TestCase(new[] { 1.0, 2.0, 3.0, 4.0 }, false)]
    public void Percentiles_are_validated(double[] input, bool expected) =>
        Assert.That(McpToolInput.TryParsePercentiles(input, "percentiles", 3, out _, out _), Is.EqualTo(expected));

    [TestCase("0", 18, "0")]
    [TestCase("1", 18, "0.000000000000000001")]
    [TestCase("1000000000000000000", 18, "1")]
    [TestCase("1500000000000000000", 18, "1.5")]
    [TestCase("1000000000000001234", 18, "1.000000000000001234")]
    [TestCase("12345000000", 9, "12.345")]
    [TestCase("7", 9, "0.000000007")]
    [TestCase("42", 0, "42")]
    public void Units_are_formatted_without_trailing_zeros(string amount, int decimals, string expected) =>
        Assert.That(McpTokenMetadata.FormatUnits(UInt256.Parse(amount), decimals), Is.EqualTo(expected));

    [Test]
    public void Log_cursor_round_trips_and_rejects_edits()
    {
        byte[] filter = McpLogCursor.ComputeFilterHash(BlockParameter.Earliest, BlockParameter.Latest, null, null);
        Hash256 blockHash = Keccak.Compute("block 12");
        string encoded = new McpLogCursor(12, 3, 99, filter, blockHash.BytesToArray()).Encode();

        bool decoded = McpLogCursor.TryDecode(encoded, out string? error, out McpLogCursor cursor);
        char[] edited = encoded.ToCharArray();
        edited[5] = edited[5] == 'A' ? 'B' : 'A';

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.True, error);
            Assert.That((cursor.Block, cursor.LogIndex, cursor.ToBlock), Is.EqualTo((12UL, 3UL, 99UL)));
            Assert.That(cursor.Matches(filter), Is.True);
            Assert.That(cursor.IsAt(blockHash), Is.True);
            Assert.That(cursor.IsAt(Keccak.Compute("reorged")), Is.False);
            Assert.That(McpLogCursor.TryDecode(new string(edited), out _, out _), Is.False);
            Assert.That(McpLogCursor.TryDecode(encoded + "A", out _, out _), Is.False);
            Assert.That(McpLogCursor.TryDecode("", out _, out _), Is.False);
        }
    }

    [Test]
    public void Log_filter_hash_ignores_address_and_alternative_order_but_not_positions()
    {
        Hash256 a = new(Hash);
        Hash256 b = Keccak.Compute("b");
        HashSet<AddressAsKey> addresses = [new Address("0x0000000000000000000000000000000000000001"), new Address("0x0000000000000000000000000000000000000002")];
        HashSet<AddressAsKey> reversed = [new Address("0x0000000000000000000000000000000000000002"), new Address("0x0000000000000000000000000000000000000001")];

        byte[] hash = McpLogCursor.ComputeFilterHash(BlockParameter.Earliest, BlockParameter.Latest, addresses, [[a, b]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(McpLogCursor.ComputeFilterHash(BlockParameter.Earliest, BlockParameter.Latest, reversed, [[b, a]]), Is.EqualTo(hash));
            Assert.That(McpLogCursor.ComputeFilterHash(BlockParameter.Earliest, BlockParameter.Latest, addresses, [null, [a, b]]), Is.Not.EqualTo(hash));
            Assert.That(McpLogCursor.ComputeFilterHash(BlockParameter.Earliest, new BlockParameter(5), addresses, [[a, b]]), Is.Not.EqualTo(hash));
            Assert.That(McpLogCursor.ComputeFilterHash(BlockParameter.Earliest, BlockParameter.Latest, null, [[a, b]]), Is.Not.EqualTo(hash));
        }
    }
}
