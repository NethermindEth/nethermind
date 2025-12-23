// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Taiko.Tdx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test.Tdx;

public class TdxServiceTests
{
    private ISurgeTdxConfig _config = null!;
    private ITdxsClient _client = null!;
    private TdxService _service = null!;
    private string _tempDir = null!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tdx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _config = Substitute.For<ISurgeTdxConfig>();
        _config.SocketPath.Returns("/tmp/tdxs.sock");
        _config.ConfigPath.Returns(_tempDir);

        _client = Substitute.For<ITdxsClient>();
        _client.GetMetadata().Returns(new TdxMetadata { IssuerType = "test", Metadata = null });
        _client.Issue(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(new byte[100]);

        _service = new TdxService(_config, _client, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Test]
    public void IsBootstrapped_returns_false_before_bootstrap()
    {
        _service.IsBootstrapped.Should().BeFalse();
    }

    [Test]
    public void Bootstrap_generates_keys_and_quote()
    {
        TdxGuestInfo info = _service.Bootstrap();

        info.Should().NotBeNull();
        info.IssuerType.Should().Be("test");
        info.PublicKey.Should().NotBeNullOrEmpty();
        info.Quote.Should().NotBeNullOrEmpty();
        _service.IsBootstrapped.Should().BeTrue();
    }

    [Test]
    public void Bootstrap_returns_same_info_when_called_twice()
    {
        TdxGuestInfo info1 = _service.Bootstrap();
        TdxGuestInfo info2 = _service.Bootstrap();

        info1.PublicKey.Should().Be(info2.PublicKey);
        info1.Quote.Should().Be(info2.Quote);
    }

    [Test]
    public void GetGuestInfo_returns_null_before_bootstrap()
    {
        _service.GetGuestInfo().Should().BeNull();
    }

    [Test]
    public void GetGuestInfo_returns_info_after_bootstrap()
    {
        _service.Bootstrap();
        TdxGuestInfo? info = _service.GetGuestInfo();

        info.Should().NotBeNull();
        info!.PublicKey.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void AttestBlockHash_throws_when_not_bootstrapped()
    {
        Block block = Build.A.Block.TestObject;

        Action act = () => _service.AttestBlockHash(block.Header.Hash!);

        act.Should().Throw<TdxException>().WithMessage("*not bootstrapped*");
    }

    [Test]
    public void AttestBlockHash_generates_valid_attestation()
    {
        _service.Bootstrap();
        Block block = Build.A.Block
            .WithNumber(100)
            .WithStateRoot(TestItem.KeccakA)
            .WithParent(Build.A.BlockHeader.TestObject)
            .TestObject;

        BlockHashTdxAttestation attestation = _service.AttestBlockHash(block.Header.Hash!);

        attestation.Should().NotBeNull();
        attestation.Signature.Should().HaveCount(85); // 20 + 65
        attestation.BlockHash.Should().Be(block.Hash!);
    }

    [Test]
    public void AttestBlockHeader_generates_valid_attestation()
    {
        _service.Bootstrap();
        Block block = Build.A.Block
            .WithNumber(100)
            .WithStateRoot(TestItem.KeccakA)
            .WithParent(Build.A.BlockHeader.TestObject)
            .TestObject;

        BlockHeaderTdxAttestation attestation = _service.AttestBlockHeader(block.Header);

        attestation.Should().NotBeNull();
        attestation.Signature.Should().HaveCount(85); // 20 + 65
        attestation.HeaderRlp.Should().NotBeEmpty();
    }

    [Test]
    public void AttestBlockHash_proof_contains_address_and_signature()
    {
        TdxGuestInfo info = _service.Bootstrap();
        Block block = Build.A.Block.WithNumber(1).TestObject;

        BlockHashTdxAttestation attestation = _service.AttestBlockHash(block.Header.Hash!);

        byte[] addressBytes = attestation.Signature[0..20];
        Address proofAddress = new(addressBytes);

        proofAddress.ToString().ToLowerInvariant().Should().Be(info.PublicKey.ToLowerInvariant());
    }

    [Test]
    public void AttestBlockHash_different_blocks_produce_different_hashes()
    {
        _service.Bootstrap();

        Block block1 = Build.A.Block.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithStateRoot(TestItem.KeccakB).TestObject;

        BlockHashTdxAttestation attestation1 = _service.AttestBlockHash(block1.Header.Hash!);
        BlockHashTdxAttestation attestation2 = _service.AttestBlockHash(block2.Header.Hash!);

        attestation1.BlockHash.Should().NotBe(attestation2.BlockHash);
    }

    [Test]
    public void AttestBlockHash_same_block_produces_same_hash()
    {
        _service.Bootstrap();
        Block block = Build.A.Block.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject;

        BlockHashTdxAttestation attestation1 = _service.AttestBlockHash(block.Header.Hash!);
        BlockHashTdxAttestation attestation2 = _service.AttestBlockHash(block.Header.Hash!);

        attestation1.BlockHash.Should().Be(attestation2.BlockHash);
    }

    [Test]
    public void Bootstrap_persists_and_reloads_data()
    {
        TdxGuestInfo info1 = _service.Bootstrap();

        var newService = new TdxService(_config, _client, LimboLogs.Instance);

        newService.IsBootstrapped.Should().BeTrue();
        TdxGuestInfo? info2 = newService.GetGuestInfo();
        info2.Should().NotBeNull();
        info2!.PublicKey.Should().Be(info1.PublicKey);
    }

    [Test]
    public void Signature_v_value_is_27_or_28()
    {
        _service.Bootstrap();
        Block block = Build.A.Block.WithNumber(1).TestObject;

        BlockHashTdxAttestation attestation = _service.AttestBlockHash(block.Header.Hash!);

        // v is at position 84 (20 + 64)
        byte v = attestation.Signature[84];
        v.Should().BeOneOf((byte)27, (byte)28);
    }

    [Test]
    public void New_service_bootstrap_loads_existing_data()
    {
        TdxGuestInfo info1 = _service.Bootstrap();

        var newService = new TdxService(_config, _client, LimboLogs.Instance);
        TdxGuestInfo info2 = newService.Bootstrap();

        info2.PublicKey.Should().Be(info1.PublicKey);
        info2.Quote.Should().Be(info1.Quote);
    }

    [Test]
    public void New_service_attest_uses_persisted_keys()
    {
        TdxGuestInfo info = _service.Bootstrap();
        Block block = Build.A.Block.WithNumber(1).TestObject;

        var newService = new TdxService(_config, _client, LimboLogs.Instance);
        newService.Bootstrap();
        BlockHashTdxAttestation attestation = newService.AttestBlockHash(block.Header.Hash!);

        byte[] addressBytes = attestation.Signature[0..20];
        Address proofAddress = new(addressBytes);
        proofAddress.ToString().ToLowerInvariant().Should().Be(info.PublicKey.ToLowerInvariant());
    }
}
