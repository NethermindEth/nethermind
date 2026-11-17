// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Network;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.P2P;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;
using Snappier;

namespace Nethermind.Optimism.Test.CL;

public class OptimismCLP2PMessageIdTests
{
    private const string Topic = "/optimism/10/2/blocks";
    private const ulong ChainId = 10;

    [Test]
    public void Valid_snappy_message_id_matches_independent_computation()
    {
        byte[] payload = Enumerable.Range(0, 1000).Select(static i => (byte)(i % 7)).ToArray();
        byte[] compressed = Snappy.CompressToArray(payload);

        byte[] expected = SHA256.HashData(
            [0x01, 0x00, 0x00, 0x00, .. BitConverter.GetBytes((ulong)Topic.Length), .. Encoding.ASCII.GetBytes(Topic), .. payload])[..20];

        MessageId id = OptimismCLP2P.CalculateMessageId(CreateMessage(compressed));

        Assert.That(id.Bytes, Is.EqualTo(expected));
    }

    [Test]
    public void Valid_snappy_message_id_matches_known_vector()
    {
        byte[] compressed = [0x05, 0x10, .. "hello"u8];

        MessageId id = OptimismCLP2P.CalculateMessageId(CreateMessage(compressed));

        Assert.That(id.Bytes, Is.EqualTo(Bytes.FromHexString("0x0089d50f80a1e1668e93fef3bd0d075261438c71")));
    }

    [TestCase("0xffffffffffff", "0x7c253cded5906d0b2b3beb0da6a58f918cd547e7")]
    [TestCase("0x05ff", "0xed7e56215e25b17ad2a74ce39fc2f7e451f40dbf")]
    public void Invalid_snappy_message_id_uses_invalid_domain_and_raw_data(string data, string expectedId)
    {
        byte[] raw = Bytes.FromHexString(data);

        byte[] expected = SHA256.HashData(
            [0x00, 0x00, 0x00, 0x00, .. BitConverter.GetBytes((ulong)Topic.Length), .. Encoding.ASCII.GetBytes(Topic), .. raw])[..20];

        MessageId id = OptimismCLP2P.CalculateMessageId(CreateMessage(raw));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(id.Bytes, Is.EqualTo(expected));
            Assert.That(id.Bytes, Is.EqualTo(Bytes.FromHexString(expectedId)));
        }
    }

    public sealed record GoVector(string Name, string Input, long Length, string? Decoded, string Id)
    {
        public override string ToString() => Name;
    }

    // Results of golang/snappy v1.0.0 (op-node's decoder) and op-node's message ID on the topic above.
    // Length is the header's value, or -1 if the header is invalid; Decoded is null if Decode rejects the block.
    private static readonly GoVector[] GoVectors =
    [
        new("empty-input", "", -1, null, "a978308a771061f41d6177d237c5b74d93418b90"),
        new("header-only-0", "00", 0, "", "cbccb9a3249b688e2c6f7d83122a0cad1a9cc8fd"),
        new("header-truncated", "80", -1, null, "8253216b58917d1be6b8c27b5d97e9dacd2abcea"),
        new("len0+literal-tag", "0000", 0, null, "faefde028ee7fc968b7f3f59c3ec7fd3f8d7f83f"),
        new("len0+literal-hello", "001068656c6c6f", 0, null, "17e3d136d7201b4a38359afc52eb80de0e256d81"),
        new("hello", "051068656c6c6f", 5, "68656c6c6f", "0089d50f80a1e1668e93fef3bd0d075261438c71"),
        new("hello+00", "051068656c6c6f00", 5, null, "a0cdbb53f9c725c3abb60d4d65c6aa6ff18ecf36"),
        new("hello+literal-world", "051068656c6c6f10776f726c64", 5, null, "1266455afdf887db9a2ce857c63fa42bfae90a79"),
        new("hello+copy1", "051068656c6c6f0105", 5, null, "c2ec16dc18cb7856a470a3a6dc164739760063db"),
        new("hello+truncated-copy2", "051068656c6c6f02", 5, null, "b745046ff34339f336cbf04316911e11973dc607"),
        new("hello+literal60-no-len", "051068656c6c6ff0", 5, null, "23822ede97a50a04882ef986ecec8d0406e0c59d"),
        new("len0-5byte-header", "8080808000", 0, "", "cbccb9a3249b688e2c6f7d83122a0cad1a9cc8fd"),
        new("len0-6byte-header", "808080808000", 0, "", "cbccb9a3249b688e2c6f7d83122a0cad1a9cc8fd"),
        new("len0-10byte-header", "80808080808080808000", 0, "", "cbccb9a3249b688e2c6f7d83122a0cad1a9cc8fd"),
        new("len0-10byte-header-last01", "80808080808080808001", -1, null, "61bd178d958ff235e1cb3b2d9fcd788b69ad3bb8"),
        new("len0-10byte-header-last02", "80808080808080808002", -1, null, "4190e46673b08dff8bb10ca8c8473358840c7bbe"),
        new("len0-11byte-header", "8080808080808080808000", -1, null, "433700e2e35556b1b7c157826d86f073d0496d35"),
        new("hello-6byte-header", "8580808080001068656c6c6f", 5, "68656c6c6f", "0089d50f80a1e1668e93fef3bd0d075261438c71"),
        new("hello-6byte-header-bit35", "8580808080011068656c6c6f", -1, null, "3be609ea9e335a700529fbd121a9f03387c25c04"),
        new("len-ffffffff", "ffffffff0f", 4294967295, null, "c2685c9ec354fab04f35d1efa49fbe5a4b32aad2"),
        new("len-1ffffffff", "ffffffff1f", -1, null, "335d44227bdd93fe127a37280759aedf99882eb2"),
        new("len-2^32+1", "8180808010", -1, null, "4de8b73128a00d7ce213cad194957190f2aa63e0"),
        new("len5-copy4-truncated", "05ff", 5, null, "ed7e56215e25b17ad2a74ce39fc2f7e451f40dbf"),
        new("aaaaa-copy1-off1", "0500610101", 5, "6161616161", "5702c28699aef9fa0675bb246c9741b623641d45"),
        new("copy1-off0", "0500610100", 5, null, "4552b4a73d924696d9b9a80f60228c9b8d209b9d"),
        new("copy1-off2>d", "0500610102", 5, null, "8b56ec6376fdaae72e27de7f8d800d7d6f5b24b3"),
        new("copy1-len>dst", "0400610101", 4, null, "db579af23dd22d7f254d2edf91b2d1f1f156f3c0"),
        new("aaa-copy2-off1", "030061060100", 3, "616161", "63731cdbe3a4ee78d75dd8dbe33900a49eac8a1f"),
        new("copy2-off0", "030061060000", 3, null, "4e932a81f56f24b4ee234ce77f190a7f32ba8abf"),
        new("aaa-copy4-off1", "0300610701000000", 3, "616161", "63731cdbe3a4ee78d75dd8dbe33900a49eac8a1f"),
        new("copy4-off0", "0300610700000000", 3, null, "d3dd692dd5697545181b27431568816ebc6e1778"),
        new("copy-first-no-output", "040101", 4, null, "40752e833526a9ddc984535e5912127763c639a3"),
        new("literal>src", "051068656c6c", 5, null, "e4104a73491d6dc7e458af287d8e8d8b56687fbc"),
        new("literal>dst", "031068656c6c6f", 3, null, "9ac000a6f2e925a08278a8537315aa9a043b715a"),
        new("short-output", "061068656c6c6f", 6, null, "ed7016182455484712d5416b1844000341a3e321"),
        new("literal60-1byte-len", "03f002616263", 3, "616263", "bc162b7e05dbe15b503a1ad5bec4956e46d7ee3b"),
        new("literal61-2byte-len", "03f40200616263", 3, "616263", "bc162b7e05dbe15b503a1ad5bec4956e46d7ee3b"),
        new("literal62-3byte-len", "03f8020000616263", 3, "616263", "bc162b7e05dbe15b503a1ad5bec4956e46d7ee3b"),
        new("literal63-4byte-len", "03fc02000000616263", 3, "616263", "bc162b7e05dbe15b503a1ad5bec4956e46d7ee3b"),
        new("literal63-huge-len", "05fcffffffff68656c6c6f", 5, null, "f763c261f75aca7d120569d3fa61c528b1a27c5a"),
        new("literal60-truncated-len", "03f0", 3, null, "ef231fe5ba9c5139ef83c5b3f5f578745c90026e"),
        new("simd-overflow-16", "8a808080102430313233343536373839", -1, null, "797383f58b3b4aefda9e6f8474d4ee976e647dcb"),
        new("scalar-overflow-15", "898080801020303132333435363738", -1, null, "a7ae94603dda242c58c013bda156a63d1935a199"),
        new("simd-zero-overflow-16", "80808080100000000000000000000000", -1, null, "a7b27a1166984383bd346a171298c1492b079a04"),
        new("simd-valid-padded5-16", "8a808080002430313233343536373839", 10, "30313233343536373839", "6f8ba0612cc23df0d3d6b287909bbb49e0d37ae6"),
        new("simd-valid-padded6-16", "89808080800020303132333435363738", 9, "303132333435363738", "3c5397854d8c6a6378cfc8cf8b377495dae0156c"),
        new("literal-underfilled-at-N", "051468656c6c6f", 5, null, "857b18382c0e846751babd2ba97a2391e0594735"),
        new("hello+copy-zero", "051068656c6c6f0100", 5, null, "4b731096fc797a7b4e88353157c7945cde29f771"),
        new("copy4-high-offset", "0300610700000080", 3, null, "679b9efdbc36a2d3cb489d54949290b96af28380"),
        new("copy4-off2>d", "0300610702000000", 3, null, "e0e015f2cdad33f034a646d7e65dcac4be590004"),
        new("copy1-truncated", "05006101", 5, null, "f223ffcb4400014590376177441821876e0d5a2e"),
        new("copy2-truncated", "0300610601", 3, null, "1561d649b69f8d90bcea622f847eb2e840c4dcd6"),
        new("copy4-truncated-after-literal", "03006107010000", 3, null, "99541292acd9190aa126d4e7e5a7f62abc42cba2"),
        new("hello-padded-header-2", "85001068656c6c6f", 5, "68656c6c6f", "0089d50f80a1e1668e93fef3bd0d075261438c71"),
        new("hello-padded-header-3", "8580001068656c6c6f", 5, "68656c6c6f", "0089d50f80a1e1668e93fef3bd0d075261438c71"),
        new("hello-padded-header-4", "858080001068656c6c6f", 5, "68656c6c6f", "0089d50f80a1e1668e93fef3bd0d075261438c71"),
        new("hello-padded-header-5", "85808080001068656c6c6f", 5, "68656c6c6f", "0089d50f80a1e1668e93fef3bd0d075261438c71"),
        new("hello-padded-header-6", "8580808080001068656c6c6f", 5, "68656c6c6f", "0089d50f80a1e1668e93fef3bd0d075261438c71"),
        new("hello-padded-header-7", "858080808080001068656c6c6f", 5, "68656c6c6f", "0089d50f80a1e1668e93fef3bd0d075261438c71"),
        new("hello-padded-header-8", "85808080808080001068656c6c6f", 5, "68656c6c6f", "0089d50f80a1e1668e93fef3bd0d075261438c71"),
        new("hello-padded-header-9", "8580808080808080001068656c6c6f", 5, "68656c6c6f", "0089d50f80a1e1668e93fef3bd0d075261438c71"),
        new("hello-padded-header-10", "858080808080808080001068656c6c6f", 5, "68656c6c6f", "0089d50f80a1e1668e93fef3bd0d075261438c71"),
        new("copy2-off2>d", "030061060200", 3, null, "305d1ae4421fcbefa431f0d569449e39565efc21"),
        new("literal61-truncated-len", "03f402", 3, null, "eaa0a9116afce54453f67dfd531d7fef4ba55b63"),
        new("literal62-truncated-len", "03f80200", 3, null, "2f1330f1051c49667618358d1dee4d772bbdddfd"),
        new("literal63-truncated-len", "03fc020000", 3, null, "319876b18246d2e285da26a4bdf1bfd3c2a85696"),
        new("overflow-header-17B", "8b808080102868656c6c6f20776f726c64", -1, null, "c627c24c08c6c6e21d9afd55d7333500410c11b7"),
        new("padded5-header-17B", "8b808080002868656c6c6f20776f726c64", 11, "68656c6c6f20776f726c64", "9d88d8c7e6b43dc69d53662662307c090c77a9f5"),
        new("padded6-header-18B", "8b80808080002868656c6c6f20776f726c64", 11, "68656c6c6f20776f726c64", "9d88d8c7e6b43dc69d53662662307c090c77a9f5"),
        new("ab-copy1-off2-len5", "070461620502", 7, "61626162616261", "feb66324ea4b7877f5aab06d3f1b048f564c1b52"),
        new("abcd-copy1-off4-len4", "080c616263640104", 8, "6162636461626364", "4cb0aaeaf966f2921130b5d1ffe7bca10b27fffb"),
        new("literal63-len-byte4-set", "05fc0400000168656c6c6f", 5, null, "d4a2cc0df780f2ef17dee3a9fd659711e347770b"),
        new("literal63-wraps-to-zero", "00fcffffffff", 0, null, "e5da251500eb1611b6012ec577fd616329331fc9"),
    ];

    [TestCaseSource(nameof(GoVectors))]
    public void Decodes_like_golang_snappy(GoVector vector)
    {
        byte[] input = Bytes.FromHexString(vector.Input);

        bool hasLength = GoSnappy.TryReadDecodedLength(input, out uint length, out _);

        Assert.That(hasLength ? length : -1L, Is.EqualTo(vector.Length));
        if (!hasLength || length > 1024) return;

        byte[] output = new byte[length];
        Array.Fill(output, (byte)0xEE);
        Assert.That(GoSnappy.TryDecode(input, output) ? output.ToHexString() : null, Is.EqualTo(vector.Decoded));
    }

    [TestCaseSource(nameof(GoVectors))]
    public void Message_id_matches_op_node(GoVector vector) =>
        Assert.That(MessageIdOf(Bytes.FromHexString(vector.Input)), Is.EqualTo(Bytes.FromHexString(vector.Id)));

    // len0+literal-hello: the header declares 0 bytes, the body alone would fill exactly 5
    [Test]
    public void Decode_rejects_destination_of_other_than_declared_length() =>
        Assert.That(GoSnappy.TryDecode(Bytes.FromHexString("001068656c6c6f"), new byte[5]), Is.False);

    // A literal of byte(i % 251), then copies back into it, if any; the expected IDs come from golang/snappy
    [TestCase(70000, "f86f1101", "", 0, "35021ce86031d9b1349c261f3a348c546e58d042")] // literal70000-3byte-len
    [TestCase(70000, "fc6f110100", "", 0, "35021ce86031d9b1349c261f3a348c546e58d042")] // literal70000-4byte-len
    [TestCase(256, "f0ff", "2100", 4, "3429d8e29f5f21a3bb1aa5ce8ff328cf8716881a")] // copy1-off256
    [TestCase(256, "f0ff", "0e0001", 4, "3429d8e29f5f21a3bb1aa5ce8ff328cf8716881a")] // copy2-off256
    [TestCase(65536, "f4ffff", "0f00000100", 4, "87d4e47d6387c431f122f5f63321cbf1379346b7")] // copy4-off65536
    [TestCase(300, "f42b01", "212cfe2c01ff2c010000", 132, "d96807ddd4d6bf5d8dafc0b9986548e2e291a5db")] // literal300-three-copies
    public void Decodes_long_offsets_like_golang_snappy(int literalLength, string literalTag, string copies, int copiedLength, string expectedId)
    {
        byte[] literal = Pattern(literalLength);
        byte[] expected = [.. literal, .. literal[..copiedLength]];
        byte[] block = [.. LengthHeader(expected.Length), .. Bytes.FromHexString(literalTag), .. literal, .. Bytes.FromHexString(copies)];
        byte[] output = new byte[expected.Length];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GoSnappy.TryDecode(block, output), Is.True);
            Assert.That(output, Is.EqualTo(expected));
            Assert.That(MessageIdOf(block), Is.EqualTo(Bytes.FromHexString(expectedId)));
        }
    }

    [Test]
    public void Message_id_limits_decoded_length_to_10_MiB()
    {
        byte[] max = Snappy.CompressToArray(Pattern(10 * 1024 * 1024));
        byte[] tooLarge = Snappy.CompressToArray(Pattern(10 * 1024 * 1024 + 1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(MessageIdOf(max), Is.EqualTo(Bytes.FromHexString("c6a89d925e50cadff2c2fe0a5fc8f8ae71f17e00")));
            Assert.That(MessageIdOf(tooLarge), Is.EqualTo(ExpectedMessageId(validDomain: false, tooLarge)));
        }
    }

    private static IEnumerable<TestCaseData> SnappierPayloads()
    {
        Random random = new(42);
        foreach (int length in (int[])[0, 1, 15, 16, 17, 64 * 1024 + 1])
        {
            byte[] payload = new byte[length];
            random.NextBytes(payload);
            yield return new TestCaseData(payload).SetArgDisplayNames($"{length} random bytes");
        }

        byte[] chunk = new byte[300];
        random.NextBytes(chunk);
        yield return new TestCaseData(Enumerable.Repeat(chunk, 700).SelectMany(static c => c).ToArray()).SetArgDisplayNames("repeated chunk");
        yield return new TestCaseData(new byte[1024 * 1024]).SetArgDisplayNames("1 MiB of zeros");
    }

    [TestCaseSource(nameof(SnappierPayloads))]
    public void Decodes_snappier_blocks_with_any_header_length(byte[] payload)
    {
        Assert.That(Snappy.DecompressToArray(Snappy.CompressToArray(payload)), Is.EqualTo(payload));

        foreach (int headerLength in (int[])[1, 6, 10])
        {
            byte[] block = CompressWithHeader(payload, headerLength);
            byte[] output = new byte[payload.Length];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(GoSnappy.TryDecode(block, output), Is.True, $"{headerLength}-byte header");
                Assert.That(output, Is.EqualTo(payload));
                Assert.That(MessageIdOf(block), Is.EqualTo(ExpectedMessageId(validDomain: true, payload)));
            }
        }
    }

    [Test]
    public void Truncated_or_extended_blocks_are_invalid()
    {
        Random random = new(7);
        byte[] payload = Enumerable.Range(0, 2000).Select(i => i % 3 == 0 ? (byte)random.Next(256) : (byte)(i % 11)).ToArray();
        byte[] block = Snappy.CompressToArray(payload);
        byte[] output = new byte[payload.Length];

        for (int i = 0; i < 500; i++)
        {
            byte[] suffix = new byte[random.Next(1, 9)];
            random.NextBytes(suffix);
            byte[] corrupt = i % 2 == 0 ? block[..random.Next(block.Length)] : [.. block, .. suffix];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(GoSnappy.TryDecode(corrupt, output), Is.False, $"case {i}");
                Assert.That(MessageIdOf(corrupt), Is.EqualTo(ExpectedMessageId(validDomain: false, corrupt)), $"case {i}");
            }
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TryValidateAndDecodePayload")]
    private static extern bool TryValidateAndDecodePayload(OptimismCLP2P p2p, byte[] msg, out ExecutionPayloadV3? payload);

    [TestCase(1, "", ExpectedResult = true)]
    [TestCase(6, "", ExpectedResult = true)]
    [TestCase(10, "", ExpectedResult = true)]
    [TestCase(1, "00", ExpectedResult = false)]
    public bool Validator_accepts_what_golang_snappy_accepts(int headerLength, string suffix)
    {
        TxDecoder.Instance.RegisterDecoder(new OptimismTxDecoder());
        (string data, ExecutionPayloadV3 expected) = PayloadDecoderTests.RealPayloadsTestCases().First();
        byte[] payloadBytes = Convert.FromBase64String(data);
        // Signed as op-node signs blocks: keccak(32-byte zero domain || 32-byte chain ID || keccak(payload))
        byte[] signingData = [.. new byte[32], .. ((UInt256)ChainId).ToBigEndian(), .. Keccak.Compute(payloadBytes).BytesToArray()];
        byte[] message = [.. new Ecdsa().Sign(TestItem.PrivateKeyA, Keccak.Compute(signingData)).BytesWithRecovery, .. payloadBytes];
        using OptimismCLP2P p2p = CreateP2P(expected.Timestamp);

        bool isValid = TryValidateAndDecodePayload(p2p, [.. CompressWithHeader(message, headerLength), .. Bytes.FromHexString(suffix)], out ExecutionPayloadV3? payload);

        Assert.That(payload?.BlockHash, Is.EqualTo(isValid ? expected.BlockHash : null));
        return isValid;
    }

    [TestCase("")]
    [TestCase("80")]
    [TestCase("05ff")]
    [TestCase("ffffffffffff")]
    [TestCase("42ff")] // declares 66 bytes, so it reaches decoding
    [TestCase("051068656c6c6f")] // a valid block of 5 bytes, below the minimum
    public void Validator_rejects_corrupt_snappy_without_throwing(string data)
    {
        using OptimismCLP2P p2p = CreateP2P(0);

        Assert.That(TryValidateAndDecodePayload(p2p, Bytes.FromHexString(data), out _), Is.False);
    }

    private static OptimismCLP2P CreateP2P(ulong timestamp) => new(Substitute.For<IExecutionEngineManager>(), ChainId, [],
        Substitute.For<IOptimismConfig>(), TestItem.PrivateKeyA.Address, new Timestamper((long)timestamp), Substitute.For<IIPResolver>(), LimboLogs.Instance);

    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(static i => (byte)(i % 251)).ToArray();

    // A Snappy length header of at least minLength bytes; extra bytes are zero groups with the continuation bit set.
    private static byte[] LengthHeader(int value, int minLength = 1)
    {
        List<byte> header = [];
        uint rest = (uint)value;
        for (; rest >= 0x80 || header.Count < minLength - 1; rest >>= 7)
        {
            header.Add((byte)(rest | 0x80));
        }

        header.Add((byte)rest);
        return [.. header];
    }

    private static byte[] CompressWithHeader(byte[] payload, int minHeaderLength) =>
        [.. LengthHeader(payload.Length, minHeaderLength), .. Snappy.CompressToArray(payload)[LengthHeader(payload.Length).Length..]];

    private static byte[] MessageIdOf(byte[] data) => OptimismCLP2P.CalculateMessageId(CreateMessage(data)).Bytes;

    private static byte[] ExpectedMessageId(bool validDomain, byte[] body) =>
        SHA256.HashData([validDomain ? (byte)1 : (byte)0, 0, 0, 0, .. BitConverter.GetBytes((ulong)Topic.Length), .. Encoding.ASCII.GetBytes(Topic), .. body])[..20];

    private static Message CreateMessage(byte[] data) => new() { Topic = Topic, Data = ByteString.CopyFrom(data) };
}
