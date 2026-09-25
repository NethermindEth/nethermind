// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Mcp.Plugin.Tools.Abi;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>Unit tests of the signature codec, the known-event and revert decoders, ENS name hashing and amount formatting.</summary>
[Parallelizable(ParallelScope.All)]
public class McpAbiTests
{
    private static readonly Address From = new("0x1111111111111111111111111111111111111111");
    private static readonly Address To = new("0x2222222222222222222222222222222222222222");

    [TestCase("balanceOf(address)", "balanceOf(address)", "0x70a08231", null)]
    [TestCase("function transfer(address to, uint256 amount) external returns (bool)", "transfer(address,uint256)", "0xa9059cbb", "(bool)")]
    [TestCase("getReserves()(uint112,uint112,uint32)", "getReserves()", "0x0902f1ac", "(uint112,uint112,uint32)")]
    [TestCase("function getReserves() view returns (uint112 reserve0, uint112 reserve1, uint32 blockTimestampLast)", "getReserves()", "0x0902f1ac", "(uint112,uint112,uint32)")]
    [TestCase("totalSupply() returns (uint)", "totalSupply()", "0x18160ddd", "(uint256)")]
    [TestCase("f(tuple(address a, uint256 b)[] memory items, bytes32 id, address payable x)", "f((address,uint256)[],bytes32,address)", null, null)]
    [TestCase("  allowance ( address , address ) ( uint256 ) ", "allowance(address,address)", "0xdd62ed3e", "(uint256)")]
    public void Parses_function_signatures(string text, string canonical, string? selector, string? outputs)
    {
        McpAbiSignature signature = McpAbiSignature.Parse(text);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(signature.Kind, Is.EqualTo(McpAbiSignatureKind.Function));
            Assert.That(signature.CanonicalSignature, Is.EqualTo(canonical));
            if (selector is not null) Assert.That(signature.SelectorHex, Is.EqualTo(selector));
            Assert.That(signature.OutputTypes, Is.EqualTo(outputs));
        }
    }

    [Test]
    public void Parses_event_signatures_with_indexed_parameters()
    {
        McpAbiSignature signature = McpAbiSignature.Parse("event Transfer(address indexed from, address indexed to, uint256 value)");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(signature.Kind, Is.EqualTo(McpAbiSignatureKind.Event));
            Assert.That(signature.Hash.ToString(), Is.EqualTo("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"));
            Assert.That(signature.IndexedCount, Is.EqualTo(2));
            Assert.That(signature.Inputs[2].Name, Is.EqualTo("value"));
            Assert.That(signature.ToString(), Is.EqualTo("event Transfer(address indexed from, address indexed to, uint256 value)"));
        }
    }

    [TestCase("", "empty")]
    [TestCase("foo(MyStruct)", "unknown type 'MyStruct'")]
    [TestCase("foo(uint7)", "integer width 7")]
    [TestCase("foo(uint264)", "integer width 264")]
    [TestCase("foo(bytes33)", "bytes33")]
    [TestCase("foo(fixed128x18)", "not supported")]
    [TestCase("foo(uint256", "missing ')'")]
    [TestCase("foo(uint256 indexed x)", "only valid in event")]
    [TestCase("foo(uint256[0])", "at least 1")]
    [TestCase("foo(uint256[1000000000])", "too large")]
    [TestCase("foo(uint256[][][][][][][][][])", "deeper than 8")]
    [TestCase("foo() banana", "unexpected 'banana'")]
    [TestCase("foo(())", "1 to 64 components")]
    [TestCase("123()", "expected a function name")]
    public void Rejects_invalid_signatures_with_a_clear_message(string text, string expected)
    {
        bool parsed = McpAbiSignature.TryParse(text, McpAbiSignatureKind.Function, out _, out string? error);

        Assert.That(parsed, Is.False);
        Assert.That(error, Does.Contain(expected));
    }

    [Test]
    public void Encodes_the_solidity_documentation_static_and_dynamic_example()
    {
        // From the Solidity ABI specification: f(uint256,uint32[],bytes10,bytes) with (0x123, [0x456, 0x789], "1234567890", "Hello, world!").
        McpAbiSignature signature = McpAbiSignature.Parse("f(uint256,uint32[],bytes10,bytes)");
        byte[] data = Encode(signature, """["0x123", ["0x456", 1929], "0x31323334353637383930", "0x48656c6c6f2c20776f726c6421"]""");

        Assert.That(data.ToHexString(true), Is.EqualTo("0x8be65246" +
            "0000000000000000000000000000000000000000000000000000000000000123" +
            "0000000000000000000000000000000000000000000000000000000000000080" +
            "3132333435363738393000000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000000000000000e0" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000000000000000000000000000000000000000000456" +
            "0000000000000000000000000000000000000000000000000000000000000789" +
            "000000000000000000000000000000000000000000000000000000000000000d" +
            "48656c6c6f2c20776f726c642100000000000000000000000000000000000000"));
    }

    [Test]
    public void Encodes_and_decodes_the_solidity_documentation_nested_dynamic_example()
    {
        // From the Solidity ABI specification: g(uint256[][],string[]) with ([[1, 2], [3]], ["one", "two", "three"]).
        McpAbiSignature signature = McpAbiSignature.Parse("g(uint256[][],string[])");
        byte[] data = Encode(signature, """[[[1, 2], [3]], ["one", "two", "three"]]""");

        const string expected = "0x2289b18c" +
            "0000000000000000000000000000000000000000000000000000000000000040" +
            "0000000000000000000000000000000000000000000000000000000000000140" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000000000000000000000000000000000000000000040" +
            "00000000000000000000000000000000000000000000000000000000000000a0" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000000000000000000000000000000000000000000001" +
            "0000000000000000000000000000000000000000000000000000000000000002" +
            "0000000000000000000000000000000000000000000000000000000000000001" +
            "0000000000000000000000000000000000000000000000000000000000000003" +
            "0000000000000000000000000000000000000000000000000000000000000003" +
            "0000000000000000000000000000000000000000000000000000000000000060" +
            "00000000000000000000000000000000000000000000000000000000000000a0" +
            "00000000000000000000000000000000000000000000000000000000000000e0" +
            "0000000000000000000000000000000000000000000000000000000000000003" +
            "6f6e650000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000003" +
            "74776f0000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000005" +
            "7468726565000000000000000000000000000000000000000000000000000000";
        Assert.That(data.ToHexString(true), Is.EqualTo(expected));

        Assert.That(McpAbiCodec.TryDecode(signature.Inputs, data.AsSpan(4), out object?[]? values, out string? error), Is.True, error);
        Assert.That(McpAbiCodec.FormatValue(new List<object?>(values!)), Is.EqualTo("[[[1, 2], [3]], [one, two, three]]"));
    }

    [Test]
    public void Round_trips_every_supported_type()
    {
        McpAbiSignature signature = McpAbiSignature.Parse(
            "f(address a, bool b, uint8 c, int24 d, int256 e, bytes4 f, bytes g, string h, (address to, uint96 amount)[] i, uint16[2] j, (string s, bool[] t) k)");
        string args = """
            ["0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed", true, "255", "-8388608", "-0x10", "0xdeadbeef", "0x", "héllo",
             [{"to": "0x1111111111111111111111111111111111111111", "amount": "79228162514264337593543950335"}, ["0x2222222222222222222222222222222222222222", 0]],
             ["0x1", 65535], ["", [false, true]]]
            """;
        byte[] data = Encode(signature, args);

        Assert.That(McpAbiCodec.TryDecode(signature.Inputs, data.AsSpan(4), out object?[]? values, out string? error), Is.True, error);
        Assert.That(McpAbiCodec.FormatValue(new List<object?>(values!)), Is.EqualTo(
            "[0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed, true, 255, -8388608, -16, 0xdeadbeef, 0x, héllo, " +
            "[[0x1111111111111111111111111111111111111111, 79228162514264337593543950335], [0x2222222222222222222222222222222222222222, 0]], " +
            "[1, 65535], [, [false, true]]]"));
    }

    [TestCase("f(uint256,uint256)", "[1]", "expected 2 argument(s) (uint256, uint256) but got 1")]
    [TestCase("f(uint8 x)", "[256]", "args[0] (x): 256 is out of range for uint8 (0 to 255)")]
    [TestCase("f(int8)", "[\"-129\"]", "out of range for int8 (-128 to 127)")]
    [TestCase("f(uint256)", "[\"-1\"]", "out of range for uint256")]
    [TestCase("f(uint256)", "[\"1.5\"]", "expected uint256 as a decimal string")]
    [TestCase("f(uint256)", "[1e3]", "expected uint256 as a decimal string")]
    [TestCase("f(address)", "[\"0x123\"]", "expected address (0x followed by 40 hex characters)")]
    [TestCase("f(address)", "[\"0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAeD\"]", "invalid EIP-55 checksum")]
    [TestCase("f(bytes4)", "[\"0xdead\"]", "exactly 4 bytes")]
    [TestCase("f(bytes)", "[\"0xabc\"]", "even number of digits")]
    [TestCase("f(bool)", "[1]", "expected bool")]
    [TestCase("f(string)", "[1]", "expected string")]
    [TestCase("f(uint256[2])", "[[1]]", "exactly 2 elements")]
    [TestCase("f((uint256 a, bool b))", "[{\"a\": 1}]", "missing tuple member 'b'")]
    [TestCase("f((uint256,bool))", "[{\"a\": 1}]", "unnamed components")]
    [TestCase("f(uint256[] xs)", "[[1, \"x\"]]", "args[0] (xs)[1]: expected uint256")]
    public void Encoding_errors_name_the_argument_and_the_expected_type(string signature, string args, string expected)
    {
        bool encoded = McpAbiCodec.TryEncodeCall(McpAbiSignature.Parse(signature), Parse(args), 4096, out _, out string? error);

        Assert.That(encoded, Is.False);
        Assert.That(error, Does.Contain(expected));
    }

    [Test]
    public void Encoding_respects_the_size_limit()
    {
        McpAbiSignature signature = McpAbiSignature.Parse("f(bytes)");
        string payload = "0x" + new string('a', 2 * 200);

        Assert.That(McpAbiCodec.TryEncodeCall(signature, Parse($"[\"{payload}\"]"), 4 + 32 * 9, out _, out _), Is.True, "exactly at the limit");
        Assert.That(McpAbiCodec.TryEncodeCall(signature, Parse($"[\"{payload}\"]"), 4 + 32 * 9 - 1, out _, out string? error), Is.False);
        Assert.That(error, Does.Contain("exceeds the"));
    }

    [TestCase("", "the data is empty")]
    [TestCase("0x00", "too short")]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000001000", "invalid offset")]
    [TestCase("0x0000000000000000000000000000000000000000000000000000000000000020ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "invalid length")]
    [TestCase("0x000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000ff", "invalid length")]
    public void Decoding_malformed_dynamic_data_fails_without_throwing(string hex, string expected)
    {
        bool decoded = McpAbiCodec.TryDecode([McpAbiType.String], Bytes.FromHexString(hex), out _, out string? error);

        Assert.That(decoded, Is.False);
        Assert.That(error, Does.Contain(expected));
    }

    [TestCase("address", "0x0000000000000000000000010000000000000000000000000000000000000001")]
    [TestCase("bool", "0x0000000000000000000000000000000000000000000000000000000000000002")]
    [TestCase("uint8", "0x0000000000000000000000000000000000000000000000000000000000000100")]
    [TestCase("int8", "0x0000000000000000000000000000000000000000000000000000000000000080")]
    [TestCase("bytes2", "0xabcdef0000000000000000000000000000000000000000000000000000000000")]
    [TestCase("uint256[]", "0x0000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000ffff")]
    public void Decoding_rejects_non_canonical_values(string type, string hex)
    {
        Assert.That(McpAbiSignature.TryParseType(type, out McpAbiType? abiType, out _), Is.True);
        Assert.That(McpAbiCodec.TryDecode([abiType!], Bytes.FromHexString(hex), out _, out _), Is.False);
    }

    [Test]
    public void Decoding_aliased_offsets_is_bounded()
    {
        // An outer array of 200 entries all pointing at the same 200-element inner array: 40,000 values from 13 KB.
        const int count = 200;
        List<byte> data = [];
        data.AddRange(Word(32));
        data.AddRange(Word(count));
        for (int i = 0; i < count; i++) data.AddRange(Word(count * 32));
        data.AddRange(Word(count));
        for (int i = 0; i < count; i++) data.AddRange(Word(i));

        bool decoded = McpAbiCodec.TryDecode([McpAbiType.ArrayOf(McpAbiType.ArrayOf(McpAbiType.UInt256))], data.ToArray(), out _, out string? error);

        Assert.That(decoded, Is.False);
        Assert.That(error, Does.Contain($"more than {McpAbiCodec.MaxDecodedValues} values"));
    }

    [Test]
    public void Decodes_error_string_reverts()
    {
        byte[] data = Encode(McpAbiSignature.Parse("Error(string)"), """["Insufficient balance"]""");

        McpDecodedRevert revert = McpKnownAbi.DecodeRevert(data);

        Assert.That(revert, Is.EqualTo(new McpDecodedRevert("Error", "Insufficient balance", "0x08c379a0")));
    }

    [TestCase(0x11, "panic 0x11: arithmetic overflow or underflow")]
    [TestCase(0x12, "panic 0x12: division or modulo by zero")]
    [TestCase(0x01, "panic 0x01: assertion failed (assert)")]
    [TestCase(0x32, "panic 0x32: array index out of bounds")]
    [TestCase(0x99, "panic 0x99: unknown panic code")]
    public void Decodes_panic_reverts(int code, string expected)
    {
        byte[] data = Encode(McpAbiSignature.Parse("Panic(uint256)"), $"[{code}]");

        McpDecodedRevert revert = McpKnownAbi.DecodeRevert(data);

        Assert.That(revert, Is.EqualTo(new McpDecodedRevert("Panic", expected, "0x4e487b71")));
    }

    [TestCase("0x", "Empty", null)]
    [TestCase("0xdeadbeef", "Custom", "0xdeadbeef")]
    [TestCase("0xdeadbeef00000000000000000000000000000000000000000000000000000000000000ff", "Custom", "0xdeadbeef")]
    [TestCase("0x08c379a0ffff", "Custom", "0x08c379a0")]
    [TestCase("0x4e487b71", "Custom", "0x4e487b71")]
    [TestCase("0xab", "Custom", null)]
    public void Decodes_custom_empty_and_malformed_reverts(string hex, string kind, string? selector)
    {
        McpDecodedRevert revert = McpKnownAbi.DecodeRevert(Bytes.FromHexString(hex));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(revert.Kind, Is.EqualTo(kind));
            Assert.That(revert.Selector, Is.EqualTo(selector));
            Assert.That(revert.Message, Is.Not.Empty);
        }
    }

    [Test]
    public void Error_string_reverts_are_sanitized()
    {
        byte[] data = Encode(McpAbiSignature.Parse("Error(string)"), "[\"line1\\nline2\\u0000\"]");

        Assert.That(McpKnownAbi.DecodeRevert(data).Message, Is.EqualTo("line1 line2 "));
    }

    [Test]
    public void Decodes_erc20_and_erc721_transfers_by_topic_count()
    {
        LogEntry erc20 = new(To, Word(1500), [McpKnownAbi.TransferTopic, Topic(From), Topic(To)]);
        LogEntry erc721 = new(To, [], [McpKnownAbi.TransferTopic, Topic(From), Topic(To), new Hash256(Word(42))]);

        McpDecodedLog? fungible = McpKnownAbi.TryDecodeLog(erc20);
        McpDecodedLog? nft = McpKnownAbi.TryDecodeLog(erc721);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fungible?.Standard, Is.EqualTo("ERC-20"));
            Assert.That(fungible?.Signature, Is.EqualTo("Transfer(address,address,uint256)"));
            Assert.That(fungible?.Get("from"), Is.EqualTo(From.ToString(true, true)));
            Assert.That(fungible?.Get("value"), Is.EqualTo("1500"));
            Assert.That(fungible?.Params.Select(static p => p.Indexed), Is.EqualTo(new[] { true, true, false }));
            Assert.That(nft?.Standard, Is.EqualTo("ERC-721"));
            Assert.That(nft?.Get("tokenId"), Is.EqualTo("42"));
        }
    }

    [Test]
    public void Decodes_other_known_events()
    {
        Hash256 transferBatch = Keccak.Compute("TransferBatch(address,address,address,uint256[],uint256[])");
        byte[] batchData = McpAbiCodec.TryEncode(
            [new(string.Empty, McpAbiType.ArrayOf(McpAbiType.UInt256)), new(string.Empty, McpAbiType.ArrayOf(McpAbiType.UInt256))],
            Parse("[[1, 2], [10, 20]]"), 4096, out byte[]? encoded, out _) ? encoded : [];
        Hash256 v3Swap = Keccak.Compute("Swap(address,address,int256,int256,uint160,uint128,int24)");
        byte[] swapData = McpAbiCodec.TryEncode(
            McpAbiSignature.Parse("f(int256,int256,uint160,uint128,int24)").Inputs,
            Parse("""["-1000", "2000", "79228162514264337593543950336", "5", "-887272"]"""), 4096, out byte[]? swap, out _) ? swap : [];

        McpDecodedLog? batch = McpKnownAbi.TryDecodeLog(new LogEntry(To, batchData, [transferBatch, Topic(From), Topic(From), Topic(To)]));
        McpDecodedLog? deposit = McpKnownAbi.TryDecodeLog(new LogEntry(To, Word(7), [Keccak.Compute("Deposit(address,uint256)"), Topic(From)]));
        McpDecodedLog? swapped = McpKnownAbi.TryDecodeLog(new LogEntry(To, swapData, [v3Swap, Topic(From), Topic(To)]));
        McpDecodedLog? approvalForAll = McpKnownAbi.TryDecodeLog(new LogEntry(To, Word(1), [Keccak.Compute("ApprovalForAll(address,address,bool)"), Topic(From), Topic(To)]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch?.Standard, Is.EqualTo("ERC-1155"));
            Assert.That(McpAbiCodec.FormatValue(batch?.Get("values")), Is.EqualTo("[10, 20]"));
            Assert.That(deposit?.Standard, Is.EqualTo("WETH"));
            Assert.That(deposit?.Get("wad"), Is.EqualTo("7"));
            Assert.That(swapped?.Standard, Is.EqualTo("Uniswap V3"));
            Assert.That(swapped?.Get("amount0"), Is.EqualTo("-1000"));
            Assert.That(swapped?.Get("tick"), Is.EqualTo("-887272"));
            Assert.That(approvalForAll?.Get("approved"), Is.EqualTo(true));
        }
    }

    [Test]
    public void Malformed_or_unknown_logs_do_not_decode()
    {
        byte[] dirtyAddress = Word(1);
        dirtyAddress[0] = 1;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(McpKnownAbi.TryDecodeLog(new LogEntry(To, [], [McpKnownAbi.TransferTopic, Topic(From), Topic(To)])), Is.Null, "missing data");
            Assert.That(McpKnownAbi.TryDecodeLog(new LogEntry(To, Word(1), [McpKnownAbi.TransferTopic, new Hash256(dirtyAddress), Topic(To)])), Is.Null, "dirty address");
            Assert.That(McpKnownAbi.TryDecodeLog(new LogEntry(To, Word(1), [McpKnownAbi.TransferTopic, Topic(From)])), Is.Null, "too few topics");
            Assert.That(McpKnownAbi.TryDecodeLog(new LogEntry(To, Word(1), [Keccak.Compute("Unknown()")])), Is.Null, "unknown event");
            Assert.That(McpKnownAbi.TryDecodeLog(new LogEntry(To, Word(1), [])), Is.Null, "anonymous");
        }
    }

    [Test]
    public void User_events_decode_indexed_dynamic_values_as_hashes()
    {
        McpAbiSignature signature = McpAbiSignature.Parse("event Named(string indexed name, address indexed who, string note)", McpAbiSignatureKind.Event);
        Hash256 nameHash = Keccak.Compute("alice");
        byte[] data = McpAbiCodec.TryEncode([new(string.Empty, McpAbiType.String)], Parse("[\"hi\"]"), 4096, out byte[]? encoded, out _) ? encoded : [];

        bool decoded = McpAbiCodec.TryDecodeEvent(signature, new LogEntry(To, data, [signature.Hash, nameHash, Topic(From)]), null, out McpDecodedLog? log);

        Assert.That(decoded, Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(log!.Get("name"), Is.EqualTo(nameHash.ToString()));
            Assert.That(log.Get("who"), Is.EqualTo(From.ToString(true, true)));
            Assert.That(log.Get("note"), Is.EqualTo("hi"));
        }
    }

    [TestCase("", "0x0000000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("eth", "0x93cdeb708b7545dc668eb9280176169d1c33cfd8ed6f04690a0bcc88a93fc4ae")]
    [TestCase("foo.eth", "0xde9b09fd7c5f901e23a3f19fecc54828e9c848539801e86591bd9801b019f84f")]
    public void Computes_ensip1_namehash_vectors(string name, string expected) =>
        Assert.That(McpEns.NameHash(name).ToString(), Is.EqualTo(expected));

    [TestCase(" Vitalik.ETH ", "vitalik.eth")]
    [TestCase("_dmarc.example.eth", "_dmarc.example.eth")]
    [TestCase("a-b.eth", "a-b.eth")]
    public void Normalizes_ascii_names(string input, string expected)
    {
        Assert.That(McpEns.TryNormalize(input, out string? normalized, out string? error), Is.True, error);
        Assert.That(normalized, Is.EqualTo(expected));
    }

    [TestCase("", "empty")]
    [TestCase("a..eth", "empty label")]
    [TestCase(".eth", "empty label")]
    [TestCase("ab--c.eth", "punycode")]
    [TestCase("a_b.eth", "underscore")]
    [TestCase("a b.eth", "contains ' '")]
    [TestCase("vitàlik.eth", "non-ASCII")]
    public void Rejects_invalid_names(string input, string expected)
    {
        Assert.That(McpEns.TryNormalize(input, out _, out string? error), Is.False);
        Assert.That(error, Does.Contain(expected));
    }

    [Test]
    public void Dns_encodes_names()
    {
        Assert.That(McpEns.DnsEncode("foo.eth").ToHexString(true), Is.EqualTo("0x03666f6f0365746800"));
        Assert.That(McpEns.ReverseName(new Address("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")), Is.EqualTo("5aaeb6053f3e94c9b9a09f33669435e7ef1beaed.addr.reverse"));
    }

    [TestCase("0", 18, "0")]
    [TestCase("1", 18, "0.000000000000000001")]
    [TestCase("1500000000000000000", 18, "1.5")]
    [TestCase("1000000000000000000", 18, "1")]
    [TestCase("1000", 0, "1000")]
    [TestCase("100", 2, "1")]
    [TestCase("123456", 3, "123.456")]
    [TestCase("5", -3, "5")]
    [TestCase("115792089237316195423570985008687907853269984665640564039457584007913129639935", 18,
        "115792089237316195423570985008687907853269984665640564039457.584007913129639935")]
    public void Formats_units_exactly(string amount, int decimals, string expected) =>
        Assert.That(McpTokenMetadata.FormatUnits(UInt256.Parse(amount), decimals), Is.EqualTo(expected));

    [Test]
    public void Formats_large_decimals_and_negative_amounts()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(McpTokenMetadata.FormatUnits((UInt256)12, 255), Is.EqualTo("0." + new string('0', 253) + "12"));
            Assert.That(McpTokenMetadata.FormatUnits(new BigInteger(-1500), 3), Is.EqualTo("-1.5"));
            Assert.That(McpTokenMetadata.FormatUnits("-2000000", 6), Is.EqualTo("-2"));
            Assert.That(McpTokenMetadata.FormatUnits("not a number", 6), Is.Null);
        }
    }

    [Test]
    public void Decodes_string_and_bytes32_token_texts()
    {
        byte[] bytes32 = new byte[32];
        Encoding.ASCII.GetBytes("MKR").CopyTo(bytes32, 0);
        byte[] abiString = McpAbiCodec.TryEncode([new(string.Empty, McpAbiType.String)], Parse("[\"Maker\"]"), 4096, out byte[]? encoded, out _) ? encoded : [];
        byte[] longString = McpAbiCodec.TryEncode([new(string.Empty, McpAbiType.String)], Parse($"[\"\\u0007{new string('x', 100)}\"]"), 4096, out byte[]? longEncoded, out _) ? longEncoded : [];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(McpTokenMetadata.DecodeText(bytes32), Is.EqualTo("MKR"));
            Assert.That(McpTokenMetadata.DecodeText(abiString), Is.EqualTo("Maker"));
            Assert.That(McpTokenMetadata.DecodeText(longString), Is.EqualTo(new string('x', McpTokenMetadata.MaxTextLength)));
            Assert.That(McpTokenMetadata.DecodeText(new byte[32]), Is.Null, "all-zero bytes32");
            Assert.That(McpTokenMetadata.DecodeText([1, 2, 3]), Is.Null, "garbage");
        }
    }

    private static byte[] Encode(McpAbiSignature signature, string args)
    {
        Assert.That(McpAbiCodec.TryEncodeCall(signature, Parse(args), 1 << 20, out byte[]? data, out string? error), Is.True, error);
        return data!;
    }

    private static JsonElement[] Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(static e => e.Clone())];
    }

    private static byte[] Word(long value)
    {
        byte[] word = new byte[32];
        ((UInt256)(ulong)value).ToBigEndian(word);
        return word;
    }

    private static Hash256 Topic(Address address)
    {
        byte[] word = new byte[32];
        address.Bytes.CopyTo(word.AsSpan(12));
        return new Hash256(word);
    }
}
