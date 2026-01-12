// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    public void SignBlockHeader_throws_when_not_bootstrapped()
    {
        Block block = Build.A.Block.WithStateRoot(TestItem.KeccakA).TestObject;

        Action act = () => _service.SignBlockHeader(block.Header);

        act.Should().Throw<TdxException>().WithMessage("*not bootstrapped*");
    }

    [Test]
    public void SignBlockHeader_generates_valid_signature()
    {
        _service.Bootstrap();
        Block block = Build.A.Block
            .WithNumber(100)
            .WithStateRoot(TestItem.KeccakA)
            .WithParent(Build.A.BlockHeader.TestObject)
            .TestObject;

        TdxBlockHeaderSignature signature = _service.SignBlockHeader(block.Header);

        signature.Should().NotBeNull();
        signature.Signature.Should().HaveCount(Signature.Size);
        signature.BlockHash.Should().Be(block.Hash!);
        signature.StateRoot.Should().Be(TestItem.KeccakA);
        signature.Header.Hash.Should().Be(block.Hash!);
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
        Block block = Build.A.Block.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject;

        TdxBlockHeaderSignature signature = _service.SignBlockHeader(block.Header);

        byte v = signature.Signature[Signature.Size - 1];
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
    public void New_service_sign_uses_persisted_keys()
    {
        _service.Bootstrap();
        Block block = Build.A.Block.WithNumber(1).WithStateRoot(TestItem.KeccakA).TestObject;
        TdxBlockHeaderSignature signature1 = _service.SignBlockHeader(block.Header);

        var newService = new TdxService(_config, _client, LimboLogs.Instance);
        newService.Bootstrap();
        TdxBlockHeaderSignature signature2 = newService.SignBlockHeader(block.Header);

        signature2.Signature.Should().BeEquivalentTo(signature1.Signature);
    }
}
