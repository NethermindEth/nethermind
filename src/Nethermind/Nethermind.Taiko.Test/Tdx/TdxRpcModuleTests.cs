// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Taiko.Config;
using Nethermind.Taiko.Tdx;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Taiko.Test.Tdx;

public class TdxRpcModuleTests
{
    private ISurgeConfig _config = null!;
    private ITdxService _tdxService = null!;
    private IBlockFinder _blockFinder = null!;
    private TdxRpcModule _rpcModule = null!;

    [SetUp]
    public void Setup()
    {
        _config = Substitute.For<ISurgeConfig>();
        _tdxService = Substitute.For<ITdxService>();
        _blockFinder = Substitute.For<IBlockFinder>();

        _rpcModule = new TdxRpcModule(_config, _tdxService, _blockFinder, LimboLogs.Instance);
    }

    [Test]
    public async Task SignBlockHeader_returns_error_when_disabled()
    {
        _config.TdxEnabled.Returns(false);

        ResultWrapper<TdxBlockHeaderSignature> result = await _rpcModule.taiko_tdxSignBlockHeader(new BlockParameter(1));

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("not enabled");
    }

    [Test]
    public async Task SignBlockHeader_returns_error_when_not_bootstrapped()
    {
        _config.TdxEnabled.Returns(true);
        _tdxService.IsBootstrapped.Returns(false);

        ResultWrapper<TdxBlockHeaderSignature> result = await _rpcModule.taiko_tdxSignBlockHeader(new BlockParameter(1));

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("not bootstrapped");
    }

    [Test]
    public async Task SignBlockHeader_returns_error_when_block_not_found()
    {
        _config.TdxEnabled.Returns(true);
        _tdxService.IsBootstrapped.Returns(true);
        _blockFinder.FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>()).Returns((BlockHeader?)null);

        ResultWrapper<TdxBlockHeaderSignature> result = await _rpcModule.taiko_tdxSignBlockHeader(new BlockParameter(1));

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("not found");
    }

    [Test]
    public async Task SignBlockHeader_returns_signature_when_successful()
    {
        _config.TdxEnabled.Returns(true);
        _tdxService.IsBootstrapped.Returns(true);

        BlockHeader header = Build.A.BlockHeader.WithStateRoot(TestItem.KeccakA).TestObject;
        _blockFinder.FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>()).Returns(header);

        var signature = new TdxBlockHeaderSignature
        {
            Signature = new byte[Signature.Size],
            BlockHash = header.Hash!,
            StateRoot = header.StateRoot!,
            Header = new BlockHeaderForRpc(header)
        };
        _tdxService.SignBlockHeader(header).Returns(signature);

        ResultWrapper<TdxBlockHeaderSignature> result = await _rpcModule.taiko_tdxSignBlockHeader(new BlockParameter(1));

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Should().NotBeNull();
        result.Data!.Header.Hash.Should().Be(header.Hash!);
    }

    [Test]
    public async Task SignBlockHeader_returns_error_on_signing_failure()
    {
        _config.TdxEnabled.Returns(true);
        _tdxService.IsBootstrapped.Returns(true);

        BlockHeader header = Build.A.BlockHeader.TestObject;
        _blockFinder.FindHeader(Arg.Any<long>(), Arg.Any<BlockTreeLookupOptions>()).Returns(header);
        _tdxService.SignBlockHeader(header).Returns(_ => throw new TdxException("Signing failed"));

        ResultWrapper<TdxBlockHeaderSignature> result = await _rpcModule.taiko_tdxSignBlockHeader(new BlockParameter(1));

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("Signing failed");
    }

    [Test]
    public async Task GetTdxGuestInfo_returns_error_when_disabled()
    {
        _config.TdxEnabled.Returns(false);

        ResultWrapper<TdxGuestInfo> result = await _rpcModule.taiko_getTdxGuestInfo();

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("not enabled");
    }

    [Test]
    public async Task GetTdxGuestInfo_returns_error_when_not_bootstrapped()
    {
        _config.TdxEnabled.Returns(true);
        _tdxService.GetGuestInfo().Returns((TdxGuestInfo?)null);

        ResultWrapper<TdxGuestInfo> result = await _rpcModule.taiko_getTdxGuestInfo();

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("not bootstrapped");
    }

    [Test]
    public async Task GetTdxGuestInfo_returns_info_when_available()
    {
        _config.TdxEnabled.Returns(true);
        var info = new TdxGuestInfo
        {
            IssuerType = "test",
            PublicKey = "0x1234",
            Quote = "abcd",
            Nonce = "1234"
        };
        _tdxService.GetGuestInfo().Returns(info);

        ResultWrapper<TdxGuestInfo> result = await _rpcModule.taiko_getTdxGuestInfo();

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Should().NotBeNull();
        result.Data!.IssuerType.Should().Be("test");
    }

    [Test]
    public async Task TdxBootstrap_returns_error_when_disabled()
    {
        _config.TdxEnabled.Returns(false);

        ResultWrapper<TdxGuestInfo> result = await _rpcModule.taiko_tdxBootstrap();

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("not enabled");
    }

    [Test]
    public async Task TdxBootstrap_returns_info_when_successful()
    {
        _config.TdxEnabled.Returns(true);
        var info = new TdxGuestInfo
        {
            IssuerType = "azure",
            PublicKey = "0xabcd",
            Quote = "quote",
            Nonce = "nonce"
        };
        _tdxService.Bootstrap().Returns(info);

        ResultWrapper<TdxGuestInfo> result = await _rpcModule.taiko_tdxBootstrap();

        result.Result.ResultType.Should().Be(ResultType.Success);
        result.Data.Should().NotBeNull();
        result.Data!.IssuerType.Should().Be("azure");
    }

    [Test]
    public async Task TdxBootstrap_returns_error_on_exception()
    {
        _config.TdxEnabled.Returns(true);
        _tdxService.Bootstrap().Returns(_ => throw new TdxException("Connection failed"));

        ResultWrapper<TdxGuestInfo> result = await _rpcModule.taiko_tdxBootstrap();

        result.Result.ResultType.Should().Be(ResultType.Failure);
        result.Result.Error.Should().Contain("Connection failed");
    }

}

