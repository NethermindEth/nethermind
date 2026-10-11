// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PublicKey = Nethermind.Core.Crypto.PublicKey;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Lean.LeanTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

/// <summary>
/// The EIP-8437 ethp2p binding without sockets: framing conformance ported from <c>assets/eip-8437/check_ethp2p.py</c>,
/// libp2p TLS peer authentication, the <c>leanq</c> record entry, and the shared transport's stream and budget rules.
/// </summary>
public class LeanEthp2pTests
{
    private const int MaxMessageBytes = LeanProtocol.MaxMessageBytes;

    // Wire bytes printed by check_ethp2p.py, so both models parse byte-identical input.
    private static readonly byte[] Status = Bytes.FromHexString(
        "000000004cf84a0101a00000000000000000000000000000000000000000000000000000000000000000e1a000000000000000000000000000000000000000000000000000000000000000000183020000");
    private static readonly byte[] Control = [0x10, .. Status];
    private static readonly byte[] Prefix = Bytes.FromHexString("110000000000000007");
    private static readonly byte[] Complete = Bytes.FromHexString("0600000024e307a0000000000000000000000000000000000000000000000000000000000000000001");
    private static readonly byte[] Objects = Bytes.FromHexString("0300000007c607c4c3028080");
    private static readonly byte[] Transactions = Bytes.FromHexString("0900000006c507c3c20280");
    private static readonly byte[] ChunkFrame = Bytes.FromHexString("0500000032f107a00000000000000000000000000000000000000000000000000000000000000000808c6f7061717565206368756e6bc0");
    private static readonly byte[] CompleteForRequestSix = Bytes.FromHexString("0600000024e306a0000000000000000000000000000000000000000000000000000000000000000001");
    private static readonly byte[] Cancel = Bytes.FromHexString("0700000002c107");

    /// <summary>The Python model's <c>Stream(request_type, highest=7, live={7})</c>.</summary>
    private sealed class Handler(int requestType = LeanMessageCode.GetChunks, ulong highest = 7, params ulong[] live) : ILeanEthp2pStreamHandler
    {
        private readonly HashSet<ulong> _live = live.Length == 0 ? [7] : [.. live];
        public List<int> Messages { get; } = [];

        public LeanResponseTarget OpenResponse(ulong requestId) =>
            requestId == 0 || requestId > highest ? LeanResponseTarget.Invalid
            : !_live.Contains(requestId) ? LeanResponseTarget.Retired
            : requestType switch
            {
                LeanMessageCode.GetObjects => LeanResponseTarget.GetObjects,
                LeanMessageCode.GetTransactions => LeanResponseTarget.GetTransactions,
                _ => LeanResponseTarget.GetChunks
            };

        public void OnControl(LeanMessage message) => Messages.Add(message.PacketType);
        public void OnChunk(in LeanChunkView chunk) => Messages.Add(LeanMessageCode.Chunk);
    }

    private static (LeanEthp2pStreamReader Reader, Handler Handler) Stream(int requestType = LeanMessageCode.GetChunks, params ulong[] live)
    {
        Handler handler = new(requestType, 7, live);
        return (new LeanEthp2pStreamReader(handler), handler);
    }

    private static List<int> Received(LeanEthp2pStreamReader reader, Handler handler) =>
        reader.Terminal is { } terminal ? [.. handler.Messages, terminal.PacketType] : handler.Messages;

    private static byte[] Frame(int messageId, int length, byte[]? payload = null) =>
        [(byte)messageId, (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length, .. payload ?? []];

    [Test]
    public void Fixed_encodings_match_the_eip()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanEthp2pProtocol.Frame(LeanMessageCode.Cancel, LeanEthp2pProtocol.Encode(new CancelMessage(7))).ToHexString(),
                Is.EqualTo("0700000002c107"));
            Assert.That(LeanEthp2pProtocol.Frame(LeanMessageCode.Status,
                LeanEthp2pProtocol.Encode(new LeanStatusMessage(1, default, [default], 1, MaxMessageBytes))), Is.EqualTo(Status));
            Assert.That(Control[..1].ToHexString(), Is.EqualTo("10"));
            Assert.That(LeanEthp2pProtocol.Frame(LeanMessageCode.Complete, LeanEthp2pProtocol.Encode(new CompleteMessage(7, default,
                LeanCompleteStatus.Unavailable)), responsePrefix: true, requestId: 7), Is.EqualTo((byte[])[.. Prefix, .. Complete]));
        }
    }

    [Test]
    public void Receive_windows_hold_a_maximal_frame_and_reserve_capacity_for_every_stream()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanEthp2pProtocol.StreamReceiveWindow, Is.GreaterThanOrEqualTo(LeanEthp2pProtocol.FrameHeaderBytes + LeanProtocol.MaxMessageBytes));
            Assert.That(LeanEthp2pProtocol.ConnectionReceiveWindow,
                Is.GreaterThanOrEqualTo(LeanEthp2pProtocol.MaxInboundStreams * LeanEthp2pProtocol.StreamReceiveWindow));
            Assert.That(int.IsPow2(LeanEthp2pProtocol.StreamReceiveWindow) && int.IsPow2(LeanEthp2pProtocol.ConnectionReceiveWindow), Is.True);
        }
    }

    private static IEnumerable<TestCaseData> Fragmented()
    {
        yield return new TestCaseData((byte[])[.. Control, .. Cancel], LeanMessageCode.GetChunks, LeanMessageCode.Cancel).SetName("Control and Cancel");
        yield return new TestCaseData((byte[])[.. Prefix, .. Complete], LeanMessageCode.GetChunks, LeanMessageCode.Complete).SetName("Complete");
        yield return new TestCaseData((byte[])[.. Prefix, .. Objects], LeanMessageCode.GetObjects, LeanMessageCode.Objects).SetName("Objects");
        yield return new TestCaseData((byte[])[.. Prefix, .. Transactions], LeanMessageCode.GetTransactions, LeanMessageCode.Transactions).SetName("Transactions");
    }

    [TestCaseSource(nameof(Fragmented))]
    public void Every_split_and_byte_by_byte_delivery_parses_the_same(byte[] wire, int requestType, int last)
    {
        for (int split = 0; split <= wire.Length; split++)
        {
            (LeanEthp2pStreamReader reader, Handler handler) = Stream(requestType);
            reader.Feed(wire.AsSpan(0, split));
            reader.Feed(wire.AsSpan(split));
            Assert.That(Received(reader, handler).Last(), Is.EqualTo(last), $"split {split}");
            if (reader.Type == LeanEthp2pProtocol.ResponseStream) Assert.That(reader.Finish(), Is.EqualTo(LeanEthp2pStreamEnd.Complete));
        }
        (LeanEthp2pStreamReader bytewise, Handler bytewiseHandler) = Stream(requestType);
        foreach (byte b in wire) bytewise.Feed([b]);
        Assert.That(Received(bytewise, bytewiseHandler).Last(), Is.EqualTo(last));
    }

    private static IEnumerable<TestCaseData> BadHeaders()
    {
        yield return new TestCaseData(Control, 10, 1, LeanMessageCode.GetChunks).SetName("Unknown message ID");
        yield return new TestCaseData(Control, LeanMessageCode.Chunk, 1, LeanMessageCode.GetChunks).SetName("Chunk on the control stream");
        yield return new TestCaseData(Prefix, LeanMessageCode.Cancel, 1, LeanMessageCode.GetChunks).SetName("Cancel on a response stream");
        yield return new TestCaseData(Prefix, LeanMessageCode.Objects, 1, LeanMessageCode.GetChunks).SetName("Objects answers GetChunks");
        yield return new TestCaseData(Control, LeanMessageCode.Cancel, 0, LeanMessageCode.GetChunks).SetName("Empty payload");
        yield return new TestCaseData(Control, LeanMessageCode.Cancel, MaxMessageBytes + 1, LeanMessageCode.GetChunks).SetName("Above MAX_MESSAGE_BYTES");
        yield return new TestCaseData(Prefix, LeanMessageCode.Objects, LeanProtocol.MaxMetadataResponseBytes + 1, LeanMessageCode.GetObjects)
            .SetName("Objects above its ceiling");
        yield return new TestCaseData(Prefix, LeanMessageCode.Transactions, LeanProtocol.MaxTxResponseBytes + 1, LeanMessageCode.GetTransactions)
            .SetName("Transactions above its ceiling");
    }

    [TestCaseSource(nameof(BadHeaders))]
    public void Header_is_rejected_before_any_payload_byte_is_read(byte[] prefix, int messageId, int length, int requestType)
    {
        (LeanEthp2pStreamReader reader, _) = Stream(requestType);
        reader.Feed(prefix);
        long before = reader.PayloadBytesRead;
        Assert.Throws<LeanEthp2pViolationException>(() => reader.Feed(Frame(messageId, length, "unread payload"u8.ToArray())));
        Assert.That(reader.PayloadBytesRead, Is.EqualTo(before));
    }

    [TestCase("110000000000000000", TestName = "Request ID zero")]
    [TestCase("110000000000000008", TestName = "Request ID above the highest issued")]
    public void Invalid_stream_prefix_is_a_violation(string prefix) =>
        Assert.Throws<LeanEthp2pViolationException>(() => Stream().Reader.Feed(Bytes.FromHexString(prefix)));

    [Test]
    public void Broadcast_selector_is_handed_to_the_dispatcher_unread()
    {
        LeanEthp2pStreamReader reader = Stream().Reader;
        LeanEthp2pOtherStreamException other = Assert.Throws<LeanEthp2pOtherStreamException>(() => reader.Feed([0x02, 0x08, 0x01]))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(other.Selector, Is.EqualTo(LeanEthp2pProtocol.SessStream));
            Assert.That(reader.PayloadBytesRead, Is.Zero);
        }
    }

    [TestCase(LeanMessageCode.Chunk, LeanMessageCode.GetChunks, MaxMessageBytes)]
    [TestCase(LeanMessageCode.Objects, LeanMessageCode.GetObjects, LeanProtocol.MaxMetadataResponseBytes)]
    [TestCase(LeanMessageCode.Transactions, LeanMessageCode.GetTransactions, LeanProtocol.MaxTxResponseBytes)]
    public void Exact_payload_ceiling_is_accepted_without_reading_the_payload(int messageId, int requestType, int limit)
    {
        (LeanEthp2pStreamReader reader, _) = Stream(requestType);
        reader.Feed([.. Prefix, .. Frame(messageId, limit)]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.InPartialUnit, Is.True);
            Assert.That(reader.PayloadBytesRead, Is.Zero);
        }
        reader.Abandon();
    }

    [TestCase("c181")]
    [TestCase("f80107")]
    [TestCase("f9000107")]
    [TestCase("c28107")]
    [TestCase("c10700")]
    [TestCase("07")]
    [TestCase("c0")]
    [TestCase("c20708")]
    [TestCase("c100")]
    [TestCase("c3820007")]
    [TestCase("c1c0")]
    [TestCase("ca89010000000000000000")]
    [TestCase("f8410707070707070707070707070707070707070707070707070707070707070707070707070707070707070707070707070707070707070707070707070707070707")]
    public void Noncanonical_or_misshapen_rlp_is_a_violation(string payload)
    {
        byte[] data = Bytes.FromHexString(payload);
        Assert.Throws<LeanEthp2pViolationException>(() => Stream().Reader.Feed([.. Control, .. Frame(LeanMessageCode.Cancel, data.Length, data)]));
    }

    [Test]
    public void Fin_before_the_terminal_response_is_incomplete_and_extra_bytes_after_it_are_violations()
    {
        byte[] wire = [.. Prefix, .. Complete];
        for (int end = 0; end < wire.Length; end++)
        {
            (LeanEthp2pStreamReader reader, _) = Stream();
            reader.Feed(wire.AsSpan(0, end));
            Assert.That(reader.Finish(), Is.EqualTo(LeanEthp2pStreamEnd.Incomplete), $"FIN after {end} bytes");
            reader.Abandon();
        }
        foreach (byte[] suffix in (byte[][])[[0x00], Complete])
            Assert.Throws<LeanEthp2pViolationException>(() => Stream().Reader.Feed([.. wire, .. suffix]));
        LeanEthp2pStreamReader control = Stream().Reader;
        control.Feed(Control);
        Assert.Throws<LeanEthp2pViolationException>(() => control.Finish(), "the control stream must stay open");
    }

    [Test]
    public void Stream_roles_and_ids_are_enforced_and_retired_prefixes_are_discarded_unread()
    {
        Assert.Throws<LeanEthp2pViolationException>(() => Stream().Reader.Feed([0x10, .. Cancel]), "Cancel before Status");
        Assert.Throws<LeanEthp2pViolationException>(() => Stream().Reader.Feed([.. Control, .. Status]), "second Status");
        Assert.Throws<LeanEthp2pViolationException>(() => Stream().Reader.Feed([.. Prefix, .. CompleteForRequestSix]), "frame/prefix ID mismatch");

        (LeanEthp2pStreamReader retired, Handler handler) = Stream(LeanMessageCode.GetChunks, 99);
        retired.Feed([.. Prefix, .. "not parsed as a frame or RLP"u8]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retired.IsDiscarding, Is.True);
            Assert.That(retired.PayloadBytesRead, Is.Zero);
            Assert.That(handler.Messages, Is.Empty);
            Assert.That(retired.Finish(), Is.EqualTo(LeanEthp2pStreamEnd.Discarded));
        }
    }

    [Test]
    public void Chunks_precede_the_terminal_complete_which_needs_fin()
    {
        (LeanEthp2pStreamReader reader, Handler handler) = Stream();
        foreach (byte b in (byte[])[.. Prefix, .. ChunkFrame, .. Complete]) reader.Feed([b]);
        Assert.That(Received(reader, handler), Is.EqualTo(new[] { LeanMessageCode.Chunk, LeanMessageCode.Complete }));
        Assert.That(reader.Finish(), Is.EqualTo(LeanEthp2pStreamEnd.Complete));

        (LeanEthp2pStreamReader incomplete, Handler incompleteHandler) = Stream();
        incomplete.Feed([.. Prefix, .. ChunkFrame]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(incomplete.Finish(), Is.EqualTo(LeanEthp2pStreamEnd.Incomplete));
            Assert.That(incompleteHandler.Messages, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Response_streams_advance_independently_in_any_order()
    {
        // Requests 1 and 2 are live; the stream for 2 completes before the one for 1 has sent a byte past its prefix.
        Handler handler = new(LeanMessageCode.GetChunks, 2, 1, 2);
        LeanEthp2pStreamReader first = new(handler), second = new(handler);
        first.Feed(Bytes.FromHexString("110000000000000001"));
        second.Feed([.. Bytes.FromHexString("110000000000000002"), .. LeanEthp2pProtocol.Frame(LeanMessageCode.Complete,
            LeanEthp2pProtocol.Encode(new CompleteMessage(2, default, LeanCompleteStatus.Unavailable)))]);
        first.Feed(LeanEthp2pProtocol.Frame(LeanMessageCode.Complete, LeanEthp2pProtocol.Encode(new CompleteMessage(1, default, LeanCompleteStatus.Busy))));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Finish(), Is.EqualTo(LeanEthp2pStreamEnd.Complete));
            Assert.That(first.Finish(), Is.EqualTo(LeanEthp2pStreamEnd.Complete));
            Assert.That(((CompleteMessage)first.Terminal!).RequestId, Is.EqualTo(1));
            Assert.That(((CompleteMessage)second.Terminal!).RequestId, Is.EqualTo(2));
        }
    }

    // Test vectors of the pinned libp2p TLS specification (tls/tls.md at f201689).
    private const string Secp256k1Certificate =
        "308201ba3082015fa0030201020204499602d2300a06082a8648ce3d040302302031123010060355040a13096c69627032702e696f310a300806035504051301313020170d3735303130313133303030305a180f34303936303130313133303030305a302031123010060355040a13096c69627032702e696f310a300806035504051301313059301306072a8648ce3d020106082a8648ce3d030107034200040c901d423c831ca85e27c73c263ba132721bb9d7a84c4f0380b2a6756fd601331c8870234dec878504c174144fa4b14b66a651691606d8173e55bd37e381569ea38184308181307f060a2b0601040183a25a01010471306f0425080212210206dc6968726765b820f050263ececf7f71e4955892776c0970542efd689d2382044630440220145e15a991961f0d08cd15425bb95ec93f6ffa03c5a385eedc34ecf464c7a8ab022026b3109b8a3f40ef833169777eb2aa337cfb6282f188de0666d1bcec2a4690dd300a06082a8648ce3d0403020349003046022100e1a217eeef9ec9204b3f774a08b70849646b6a1e6b8b27f93dc00ed58545d9fe022100b00dafa549d0f03547878338c7b15e7502888f6d45db387e5ae6b5d46899cef0";

    private const string Ed25519Certificate =
        "308201ae30820156a0030201020204499602d2300a06082a8648ce3d040302302031123010060355040a13096c69627032702e696f310a300806035504051301313020170d3735303130313133303030305a180f34303936303130313133303030305a302031123010060355040a13096c69627032702e696f310a300806035504051301313059301306072a8648ce3d020106082a8648ce3d030107034200040c901d423c831ca85e27c73c263ba132721bb9d7a84c4f0380b2a6756fd601331c8870234dec878504c174144fa4b14b66a651691606d8173e55bd37e381569ea37c307a3078060a2b0601040183a25a0101046a3068042408011220a77f1d92fedb59dddaea5a1c4abd1ac2fbde7d7b879ed364501809923d7c11b90440d90d2769db992d5e6195dbb08e706b6651e024fda6cfb8846694a435519941cac215a8207792e42849cccc6cd8136c6e4bde92a58c5e08cfd4206eb5fe0bf909300a06082a8648ce3d0403020346003043021f50f6b6c52711a881778718238f650c9fb48943ae6ee6d28427dc6071ae55e702203625f116a7a454db9c56986c82a25682f7248ea1cb764d322ea983ed36a31b77";

    private const string EcdsaCertificate =
        "308201f63082019da0030201020204499602d2300a06082a8648ce3d040302302031123010060355040a13096c69627032702e696f310a300806035504051301313020170d3735303130313133303030305a180f34303936303130313133303030305a302031123010060355040a13096c69627032702e696f310a300806035504051301313059301306072a8648ce3d020106082a8648ce3d030107034200040c901d423c831ca85e27c73c263ba132721bb9d7a84c4f0380b2a6756fd601331c8870234dec878504c174144fa4b14b66a651691606d8173e55bd37e381569ea381c23081bf3081bc060a2b0601040183a25a01010481ad3081aa045f0803125b3059301306072a8648ce3d020106082a8648ce3d03010703420004bf30511f909414ebdd3242178fd290f093a551cf75c973155de0bb5a96fedf6cb5d52da7563e794b512f66e60c7f55ba8a3acf3dd72a801980d205e8a1ad29f2044730450220064ea8124774caf8f50e57f436aa62350ce652418c019df5d98a3ac666c9386a022100aa59d704a931b5f72fb9222cb6cc51f954d04a4e2e5450f8805fe8918f71eaae300a06082a8648ce3d04030203470030440220799395b0b6c1e940a7e4484705f610ab51ed376f19ff9d7c16757cfbf61b8d4302206205c03fbb0f95205c779be86581d3e31c01871ad5d1f3435bcf375cb0e5088a";

    private const string MismatchedCertificate =
        "308201f73082019da0030201020204499602d2300a06082a8648ce3d040302302031123010060355040a13096c69627032702e696f310a300806035504051301313020170d3735303130313133303030305a180f34303936303130313133303030305a302031123010060355040a13096c69627032702e696f310a300806035504051301313059301306072a8648ce3d020106082a8648ce3d030107034200040c901d423c831ca85e27c73c263ba132721bb9d7a84c4f0380b2a6756fd601331c8870234dec878504c174144fa4b14b66a651691606d8173e55bd37e381569ea381c23081bf3081bc060a2b0601040183a25a01010481ad3081aa045f0803125b3059301306072a8648ce3d020106082a8648ce3d03010703420004bf30511f909414ebdd3242178fd290f093a551cf75c973155de0bb5a96fedf6cb5d52da7563e794b512f66e60c7f55ba8a3acf3dd72a801980d205e8a1ad29f204473045022100bb6e03577b7cc7a3cd1558df0da2b117dfdcc0399bc2504ebe7de6f65cade72802206de96e2a5be9b6202adba24ee0362e490641ac45c240db71fe955f2c5cf8df6e300a06082a8648ce3d0403020348003045022100e847f267f43717358f850355bdcabbefb2cfbf8a3c043b203a14788a092fe8db022027c1d04a2d41fd6b57a7e8b3989e470325de4406e52e084e34a3fd56eef0d0df";

    [Test]
    public void Libp2p_secp256k1_vector_authenticates_its_node_key()
    {
        bool authenticated = LeanEthp2pIdentity.TryAuthenticate(Bytes.FromHexString(Secp256k1Certificate), DateTimeOffset.UtcNow,
            out PublicKey? key, out string? error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(authenticated, Is.True, error);
            Assert.That(key, Is.EqualTo(new CompressedPublicKey("0206dc6968726765b820f050263ececf7f71e4955892776c0970542efd689d2382").Decompress()));
        }
    }

    [TestCase(Ed25519Certificate, "node key is not a compressed secp256k1 key", TestName = "Ed25519 identity")]
    [TestCase(EcdsaCertificate, "node key is not a compressed secp256k1 key", TestName = "ECDSA identity")]
    [TestCase(MismatchedCertificate, "node key is not a compressed secp256k1 key", TestName = "ECDSA identity that did not sign the certificate key")]
    public void Libp2p_vectors_without_a_valid_secp256k1_identity_are_rejected(string certificate, string reason)
    {
        Assert.That(LeanEthp2pIdentity.TryAuthenticate(Bytes.FromHexString(certificate), DateTimeOffset.UtcNow, out _, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(reason));
    }

    [Test]
    public void Own_certificate_round_trips_and_tampering_or_validity_is_rejected()
    {
        using X509Certificate2 certificate = LeanEthp2pIdentity.CreateCertificate(TestItem.PrivateKeyA);
        byte[] der = certificate.RawData;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.That(LeanEthp2pIdentity.TryAuthenticate(der, now, out PublicKey? key, out string? error), Is.True, error);
        Assert.That(key, Is.EqualTo(TestItem.PrivateKeyA.PublicKey));

        byte[] tampered = der.ToArray();
        tampered[^1] ^= 1;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanEthp2pIdentity.TryAuthenticate(tampered, now, out _, out _), Is.False, "self-signature");
            Assert.That(LeanEthp2pIdentity.TryAuthenticate(der, now.AddYears(101), out _, out error), Is.False);
            Assert.That(error, Is.EqualTo("certificate has expired"));
            Assert.That(LeanEthp2pIdentity.TryAuthenticate(der, now.AddDays(-1), out _, out error), Is.False);
            Assert.That(error, Is.EqualTo("certificate is not yet valid"));
            Assert.That(LeanEthp2pIdentity.TryAuthenticate(der[..^10], now, out _, out _), Is.False, "truncated");
        }
    }

    [Test]
    public void Secp256k1_extension_signed_over_another_certificate_key_is_rejected()
    {
        using X509Certificate2 signed = LeanEthp2pIdentity.CreateCertificate(TestItem.PrivateKeyA);
        X509Extension extension = signed.Extensions[LeanEthp2pIdentity.ExtensionOid]!;
        using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("SERIALNUMBER=01", otherKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509Extension(LeanEthp2pIdentity.ExtensionOid, extension.RawData, critical: false));
        using X509Certificate2 forged = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));

        Assert.That(LeanEthp2pIdentity.TryAuthenticate(forged.RawData, DateTimeOffset.UtcNow, out _, out string? error), Is.False);
        Assert.That(error, Is.EqualTo("libp2p public key signature is invalid"));
    }

    // AlgorithmIdentifier SEQUENCE { OID } for Ed25519 (1.3.101.112) and ecdsa-with-SHA256 (1.2.840.10045.4.3.2).
    private const string Ed25519Algorithm = "300506032b6570";
    private const string EcdsaSha256Algorithm = "300a06082a8648ce3d040302";

    /// <summary>A certificate with the libp2p extension whose signature algorithm names another family than its own key.</summary>
    /// <remarks>Only the self-signature check reads the outer algorithm, so TLS still completes with the matching private key.</remarks>
    internal static X509Certificate2 MislabelledCertificate(bool rsaKey)
    {
        using X509Certificate2 libp2p = LeanEthp2pIdentity.CreateCertificate(TestItem.PrivateKeyA);
        using AsymmetricAlgorithm key = rsaKey ? RSA.Create(2048) : ECDsa.Create(ECCurve.NamedCurves.nistP256);
        X509SignatureGenerator generator = new MislabelledGenerator(key, Bytes.FromHexString(rsaKey ? EcdsaSha256Algorithm : Ed25519Algorithm));
        CertificateRequest request = new(new X500DistinguishedName("SERIALNUMBER=01"), generator.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(libp2p.Extensions[LeanEthp2pIdentity.ExtensionOid]!);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 certificate = request.Create(request.SubjectName, generator, now.AddHours(-1), now.AddHours(1), [1]);
        using X509Certificate2 withKey = key is RSA rsa ? certificate.CopyWithPrivateKey(rsa) : certificate.CopyWithPrivateKey((ECDsa)key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private sealed class MislabelledGenerator(AsymmetricAlgorithm key, byte[] algorithm) : X509SignatureGenerator
    {
        public override byte[] GetSignatureAlgorithmIdentifier(HashAlgorithmName hashAlgorithm) => algorithm;

        public override byte[] SignData(byte[] data, HashAlgorithmName hashAlgorithm) => key is RSA rsa
            ? rsa.SignData(data, hashAlgorithm, RSASignaturePadding.Pkcs1)
            : ((ECDsa)key).SignData(data, hashAlgorithm, DSASignatureFormat.Rfc3279DerSequence);

        protected override System.Security.Cryptography.X509Certificates.PublicKey BuildPublicKey() => new(key);
    }

    [TestCase(false, TestName = "Ed25519 signature algorithm over a P-256 key")]
    [TestCase(true, TestName = "ECDSA signature algorithm over an RSA key")]
    public void Signature_algorithm_of_another_key_family_is_rejected_without_throwing(bool rsaKey)
    {
        using X509Certificate2 certificate = MislabelledCertificate(rsaKey);
        bool authenticated = true;
        string? error = null;
        Assert.That(() => authenticated = LeanEthp2pIdentity.TryAuthenticate(certificate.RawData, DateTimeOffset.UtcNow, out _, out error), Throws.Nothing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(authenticated, Is.False);
            Assert.That(error, Is.EqualTo("certificate self-signature is invalid"));
        }
    }

    [Test]
    public void Identity_signatures_are_low_s_der_and_bound_to_their_message()
    {
        byte[] message = "libp2p-tls-handshake:"u8.ToArray();
        byte[] signature = LeanEthp2pIdentity.Sign(TestItem.PrivateKeyB, message);
        Org.BouncyCastle.Math.EC.ECPoint point = Org.BouncyCastle.Asn1.Sec.SecNamedCurves.GetByName("secp256k1").Curve
            .DecodePoint(TestItem.PrivateKeyB.CompressedPublicKey.Bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(signature[0], Is.EqualTo(0x30), "DER SEQUENCE");
            Assert.That(LeanEthp2pIdentity.Verify(point, message, signature), Is.True);
            Assert.That(LeanEthp2pIdentity.Verify(point, [.. message, 0], signature), Is.False);
        }
    }

    private static NodeRecord Record(PrivateKey key, EnrContentEntry? leanq, IPAddress? ip = null)
    {
        NodeRecord record = new();
        record.SetEntry(new IpEntry(ip ?? IPAddress.Loopback));
        record.SetEntry(new SecP256k1Entry(key.CompressedPublicKey));
        if (leanq is not null) record.SetEntry(leanq);
        record.EnrSequence = 5;
        new NodeRecordSigner(new Ecdsa(), key).Sign(record);
        return record;
    }

    [Test]
    public void Signed_record_with_leanq_yields_the_dial_endpoint_and_node_key()
    {
        NodeRecord record = Record(TestItem.PrivateKeyA, new LeanqEntry(30310));
        Assert.That(LeanEthp2pRecord.TryParse(record.ToString(), out LeanEthp2pRecord? parsed, out string? error), Is.True, error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed!.NodeKey, Is.EqualTo(TestItem.PrivateKeyA.PublicKey));
            Assert.That(parsed.EndPoint, Is.EqualTo(new IPEndPoint(IPAddress.Loopback, 30310)));
            Assert.That(NodeRecord.FromEnrString(record.ToString()).GetObj<byte[]>(LeanEthp2pProtocol.EnrKey)!.ToHexString(),
                Is.EqualTo("c401827666"), "leanq = [1, 30310]");
        }
    }

    private sealed class RawEntry(string key, byte[] value) : EnrContentEntry<byte[]>(value)
    {
        public override string Key => key;
        protected override int GetRlpLengthOfValue() => Value.Length;
        protected override void EncodeValue<TWriter>(ref TWriter writer) => writer.Write(Value);
    }

    [TestCase(null, TestName = "No leanq: a discovery port alone implies nothing")]
    [TestCase("c402827666", TestName = "Unknown leanq version")]
    [TestCase("c20180", TestName = "Port zero")]
    [TestCase("c50183010000", TestName = "Port above 65535")]
    [TestCase("827666", TestName = "Not a list")]
    public void Record_without_a_usable_leanq_is_not_dialled(string? leanq)
    {
        NodeRecord record = Record(TestItem.PrivateKeyA, leanq is null ? null : new RawEntry(LeanEthp2pProtocol.EnrKey, Bytes.FromHexString(leanq)));
        Assert.That(LeanEthp2pRecord.TryParse(record.ToString(), out _, out _), Is.False);
    }

    [Test]
    public void Record_with_a_bad_signature_is_not_dialled()
    {
        NodeRecord record = Record(TestItem.PrivateKeyA, new LeanqEntry(30310));
        string enr = record.ToString();
        char last = enr[^2] == 'A' ? 'B' : 'A';
        Assert.That(LeanEthp2pRecord.TryParse(string.Concat(enr.AsSpan(0, enr.Length - 2), last.ToString(), enr[^1].ToString()), out _, out _), Is.False);
    }

    [Test]
    public void Connections_of_one_node_share_request_and_serving_budgets()
    {
        using LeanTestNode node = new();
        FakeLink rlpx = new("rlpx"), quic = new("quic"), other = new("other");
        LeanPeer viaRlpx = node.Transport.Accept(rlpx, LeanTestNode.Status(), TestItem.PublicKeyA)!;
        LeanPeer viaQuic = node.Transport.Accept(quic, LeanTestNode.Status(), TestItem.PublicKeyA)!;
        LeanPeer otherNode = node.Transport.Accept(other, LeanTestNode.Status(), TestItem.PublicKeyB)!;
        // 256 chunks need eight requests: four for each node.
        byte[] body = OpaqueWrapperBody(255 * LeanProtocol.ChunkBytes);
        LeanDescriptor descriptor = Describe(body, out _);

        node.Transport.OnAnnounce(viaRlpx, new AnnounceObjectsMessage([descriptor]));
        node.Transport.OnAnnounce(viaQuic, new AnnounceObjectsMessage([descriptor]));
        node.Transport.OnAnnounce(otherNode, new AnnounceObjectsMessage([descriptor]));
        int sameNode = rlpx.Sent<GetChunksMessage>().Length + quic.Sent<GetChunksMessage>().Length;

        node.Transport.PublishWrapper(Wrapper(1000, false, FrameTransaction(40)));
        Assert.That(viaRlpx.Node, Is.SameAs(viaQuic.Node));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sameNode, Is.EqualTo(LeanProtocol.MaxRequestsPerPeer), "one request budget across both connections");
            Assert.That(other.Sent<GetChunksMessage>(), Has.Length.EqualTo(LeanProtocol.MaxRequestsPerPeer), "another node has its own");
            Assert.That(quic.Sent<GetChunksMessage>().Select(r => r.RequestId).Concat(rlpx.Sent<GetChunksMessage>().Select(r => r.RequestId)),
                Is.All.LessThanOrEqualTo(LeanProtocol.MaxRequestsPerPeer), "request IDs stay per connection");
        }
    }

    [Test]
    public void Response_stream_prefixes_resolve_against_the_connections_requests()
    {
        using LeanTestNode node = new();
        FakeLink link = new();
        LeanPeer peer = node.Connect(link);
        byte[] body = OpaqueWrapperBody(3 * LeanProtocol.ChunkBytes);
        LeanDescriptor descriptor = Describe(body, out _);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        ulong live = link.Sent<GetChunksMessage>().Single().RequestId;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.OpenResponseStream(peer, 0), Is.EqualTo(LeanResponseTarget.Invalid));
            Assert.That(node.Transport.OpenResponseStream(peer, live + 1), Is.EqualTo(LeanResponseTarget.Invalid));
            Assert.That(node.Transport.OpenResponseStream(peer, live), Is.EqualTo(LeanResponseTarget.GetChunks));
            Assert.That(node.Transport.OpenResponseStream(peer, live), Is.EqualTo(LeanResponseTarget.Duplicate));
        }
        node.Transport.OnComplete(peer, new CompleteMessage(live, descriptor.ObjectId, LeanCompleteStatus.Busy));
        Assert.That(node.Transport.OpenResponseStream(peer, live), Is.EqualTo(LeanResponseTarget.Retired));
    }

    [Test]
    public void Reset_or_early_fin_releases_the_slot_and_keeps_verified_chunks()
    {
        using LeanTestNode node = new();
        FakeLink link = new();
        LeanPeer peer = node.Connect(link);
        byte[] body = OpaqueWrapperBody(3 * LeanProtocol.ChunkBytes);
        LeanDescriptor descriptor = Describe(body, out LeanChunkTree tree);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        GetChunksMessage request = link.Sent<GetChunksMessage>().Single();
        Assert.That(node.Transport.OpenResponseStream(peer, request.RequestId), Is.EqualTo(LeanResponseTarget.GetChunks));
        node.Transport.OnChunk(peer, LeanChunkView.Parse(Wire(Chunk(request.RequestId, descriptor, body, tree, request.Indices[0]))));
        long retained = node.Transport.IncompleteBytes;

        node.Transport.OnResponseAborted(peer, request.RequestId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.IsLive(peer, request.RequestId), Is.False);
            Assert.That(node.Transport.IncompleteBytes, Is.EqualTo(retained), "verified chunks are kept");
            Assert.That(node.Transport.AssemblyCount, Is.EqualTo(1));
            Assert.That(link.Penalties, Is.Empty);
        }
    }

    [Test]
    public async Task Reset_after_cancel_releases_the_slot_without_a_terminal_response()
    {
        using LeanTestNode node = new();
        FakeLink link = new();
        LeanPeer peer = node.Connect(link);
        using CancellationTokenSource cancellation = new();
        Task<LeanObjectResult[]?> response = node.Transport.RequestObjectsAsync(peer,
            [new(LeanProtocol.KindWrapper, LeanObjectTransport.LocalProfile, LeanProtocol.LookupPrimary, TestItem.KeccakA.ValueHash256)],
            cancellation.Token);
        await cancellation.CancelAsync();
        Assert.That(await response, Is.Null);
        ulong id = link.Sent<GetObjectsMessage>().Single().RequestId;
        Assert.That(node.Transport.IsLive(peer, id), Is.True, "Cancel alone keeps the slot");
        Assert.That(node.Transport.OpenResponseStream(peer, id), Is.EqualTo(LeanResponseTarget.GetObjects));

        node.Transport.OnResponseAborted(peer, id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.Sent<CancelMessage>().Single().RequestId, Is.EqualTo(id));
            Assert.That(node.Transport.IsLive(peer, id), Is.False);
            Assert.That(link.Penalties, Is.Empty);
        }
    }

    [TestCase(LeanBinding.Rlpx, LeanBinding.Rlpx, true, TestName = "Default RLPx common binding")]
    [TestCase(LeanBinding.Ethp2p, LeanBinding.Ethp2p, true, TestName = "Ethp2p common binding without RLPx")]
    [TestCase(LeanBinding.Rlpx | LeanBinding.Ethp2p, LeanBinding.Ethp2p, true, TestName = "Both bindings during a migration")]
    [TestCase(LeanBinding.Rlpx, LeanBinding.Ethp2p, false, TestName = "Common binding not supported locally")]
    [TestCase(LeanBinding.Rlpx | LeanBinding.Ethp2p, LeanBinding.Rlpx | LeanBinding.Ethp2p, false, TestName = "Common binding is exactly one")]
    [TestCase(LeanBinding.None, LeanBinding.None, false, TestName = "No common binding")]
    public void Common_binding_must_be_one_supported_binding(LeanBinding bindings, LeanBinding common, bool valid)
    {
        NetworkConfig config = new() { LeanBindings = bindings, LeanCommonBinding = common };
        if (valid) Assert.DoesNotThrow(() => LeanEthp2pModule.Validate(config));
        else Assert.Throws<InvalidConfigurationException>(() => LeanEthp2pModule.Validate(config));
    }

    [TestCase(LeanBinding.Ethp2p, LeanBinding.Ethp2p, false, TestName = "Ethp2p common binding needs a supported platform")]
    [TestCase(LeanBinding.Rlpx | LeanBinding.Ethp2p, LeanBinding.Ethp2p, false, TestName = "RLPx does not stand in for the Ethp2p common binding")]
    [TestCase(LeanBinding.Rlpx | LeanBinding.Ethp2p, LeanBinding.Rlpx, true, TestName = "Optional Ethp2p binding on an unsupported platform")]
    public void Unsupported_platform_rejects_only_an_ethp2p_common_binding(LeanBinding bindings, LeanBinding common, bool valid)
    {
        NetworkConfig config = new() { LeanBindings = bindings, LeanCommonBinding = common };
        if (valid) Assert.DoesNotThrow(() => LeanEthp2pModule.Validate(config, platformSupported: false));
        else Assert.Throws<InvalidConfigurationException>(() => LeanEthp2pModule.Validate(config, platformSupported: false));
    }

    [TestCase(LeanBinding.Ethp2p, LeanBinding.Ethp2p, null, TestName = "Ethp2p common binding fails startup")]
    [TestCase(LeanBinding.Rlpx | LeanBinding.Ethp2p, LeanBinding.Ethp2p, null, TestName = "Ethp2p common binding fails startup with RLPx enabled")]
    [TestCase(LeanBinding.Rlpx | LeanBinding.Ethp2p, LeanBinding.Rlpx, "lean/1 continues over RLPx", TestName = "Optional binding only warns")]
    public void Binding_that_cannot_start_is_fatal_only_as_the_common_binding(LeanBinding bindings, LeanBinding common, string? warning)
    {
        NetworkConfig config = new() { LeanBindings = bindings, LeanCommonBinding = common };
        TestLogger logger = new();
        Task start = StartLeanEthp2p.Start(_ => throw new PlatformNotSupportedException("QUIC is unavailable"), config, new ILogger(logger),
            CancellationToken.None);

        if (warning is null)
        {
            Assert.That(async () => await start, Throws.InstanceOf<InvalidConfigurationException>());
            Assert.That(new StartLeanEthp2p(null!, config, LimboLogs.Instance).MustInitialize, Is.True);
        }
        else
        {
            Assert.That(async () => await start, Throws.Nothing);
            Assert.That(logger.LogList, Has.One.Contains(warning));
            Assert.That(new StartLeanEthp2p(null!, config, LimboLogs.Instance).MustInitialize, Is.False);
        }
    }

    [Test]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("windows")]
    public async Task Local_record_sequence_rises_with_every_published_change()
    {
        using LeanTestNode node = new();
        await using LeanEthp2pHost host = new(node.Transport, new NetworkConfig(), new InsecureProtectedPrivateKey(TestItem.PrivateKeyA),
            LimboLogs.Instance);
        INodeRecordProvider inner = Substitute.For<INodeRecordProvider>();
        LeanEthp2pNodeRecordProvider provider = new(inner, host, new Ecdsa(), new InsecureProtectedPrivateKey(TestItem.PrivateKeyA));
        NodeRecord first = Record(TestItem.PrivateKeyA, null);
        NodeRecord refreshed = Record(TestItem.PrivateKeyA, null, IPAddress.Parse("10.0.0.1"));
        refreshed.EnrSequence = first.EnrSequence + 1;
        new NodeRecordSigner(new Ecdsa(), TestItem.PrivateKeyA).Sign(refreshed);

        List<NodeRecord> published = [];
        async Task Publish(NodeRecord current, int? port)
        {
            inner.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(current);
            host.LocalEndPoint = port is { } p ? new IPEndPoint(IPAddress.Loopback, p) : null;
            published.Add(await provider.GetCurrentAsync());
        }

        await Publish(first, null);
        await Publish(first, 30310);
        await Publish(first, 30310);
        await Publish(first, 30311);
        await Publish(refreshed, 30311);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(published.Select(static r => r.EnrSequence), Is.EqualTo(new ulong[] { 5, 6, 6, 7, 8 }),
                "a port change and an inner record at the extension's sequence each move above it");
            Assert.That(published[2], Is.SameAs(published[1]), "an unchanged record is not re-signed");
            Assert.That(published.Skip(1).Select(static r => LeanEthp2pRecord.TryParse(r.ToString(), out LeanEthp2pRecord? parsed, out _)
                ? parsed.EndPoint.Port : 0), Is.EqualTo(new[] { 30310, 30310, 30311, 30311 }), "each extension is signed and advertises its port");
        }
    }
}
