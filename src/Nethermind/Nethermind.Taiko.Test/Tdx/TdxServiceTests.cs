// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
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
    private ISpecProvider _specProvider = null!;
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

        _specProvider = Substitute.For<ISpecProvider>();
        _service = new TdxService(_config, _client, _specProvider, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Test]
    public void IsAvailable_returns_false_before_bootstrap()
    {
        _service.IsAvailable.Should().BeFalse();
    }

    [Test]
    public void Bootstrap_generates_keys_and_quote()
    {
        TdxGuestInfo info = _service.Bootstrap();

        info.Should().NotBeNull();
        info.IssuerType.Should().Be("test");
        info.PublicKey.Should().NotBeNullOrEmpty();
        info.Quote.Should().NotBeNullOrEmpty();
        _service.IsAvailable.Should().BeTrue();
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
    public void Attest_throws_when_not_bootstrapped()
    {
        Block block = Build.A.Block.TestObject;

        Action act = () => _service.Attest(block);

        act.Should().Throw<TdxException>().WithMessage("*not bootstrapped*");
    }

    [Test]
    public void Attest_generates_valid_attestation()
    {
        _service.Bootstrap();
        Block block = Build.A.Block
            .WithNumber(100)
            .WithStateRoot(TestItem.KeccakA)
            .WithParent(Build.A.BlockHeader.TestObject)
            .TestObject;

        TdxAttestation attestation = _service.Attest(block);

        attestation.Should().NotBeNull();
        attestation.Proof.Should().HaveCount(89); // 4 + 20 + 65
        attestation.Quote.Should().NotBeEmpty();
        attestation.Block.Should().NotBeNull();
        attestation.Block.Hash.Should().Be(block.Hash!);
    }

    [Test]
    public void Attest_proof_contains_address_and_signature()
    {
        TdxGuestInfo info = _service.Bootstrap();
        Block block = Build.A.Block.WithNumber(1).TestObject;

        TdxAttestation attestation = _service.Attest(block);

        // Extract address from proof (bytes 4-24)
        byte[] addressBytes = attestation.Proof[4..24];
        Address proofAddress = new(addressBytes);

        // Address in proof should match bootstrap public key
        proofAddress.ToString().ToLowerInvariant().Should().Be(info.PublicKey.ToLowerInvariant());
    }

    [Test]
    public void Attest_different_blocks_produce_different_hashes()
    {
        _service.Bootstrap();

        Block block1 = Build.A.Block.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject;
        Block block2 = Build.A.Block.WithNumber(2).WithStateRoot(TestItem.KeccakB).TestObject;

        TdxAttestation attestation1 = _service.Attest(block1);
        TdxAttestation attestation2 = _service.Attest(block2);

        attestation1.Block.Hash.Should().NotBe(attestation2.Block.Hash!);
    }

    [Test]
    public void Attest_same_block_produces_same_hash()
    {
        _service.Bootstrap();
        Block block = Build.A.Block.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject;

        TdxAttestation attestation1 = _service.Attest(block);
        TdxAttestation attestation2 = _service.Attest(block);

        attestation1.Block.Hash.Should().Be(attestation2.Block.Hash!);
    }

    [Test]
    public void Bootstrap_persists_and_reloads_data()
    {
        TdxGuestInfo info1 = _service.Bootstrap();

        // Create new service instance with same config path
        var newService = new TdxService(_config, _client, _specProvider, LimboLogs.Instance);

        newService.IsAvailable.Should().BeTrue();
        TdxGuestInfo? info2 = newService.GetGuestInfo();
        info2.Should().NotBeNull();
        info2!.PublicKey.Should().Be(info1.PublicKey);
    }

    [Test]
    public void Signature_v_value_is_27_or_28()
    {
        _service.Bootstrap();
        Block block = Build.A.Block.WithNumber(1).TestObject;

        TdxAttestation attestation = _service.Attest(block);

        // v is at position 88 (4 + 20 + 64)
        byte v = attestation.Proof[88];
        v.Should().BeOneOf((byte)27, (byte)28);
    }
}
