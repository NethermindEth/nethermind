// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using SendBlobs;

namespace SendBlobs.Test;

[NonParallelizable]
public class FundsDistributorTests
{
    private const ulong ChainId = 1UL;
    private const string OneEtherHex = "0xde0b6b3a7640000";
    private const string ZeroNonceHex = "0x0";
    private const string OneGweiHex = "0x3b9aca00";
    private const string OkTxHashHex = "0xab123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde";
    private const ulong OneGwei = 1_000_000_000;

    private TempPath _workDir = null!;
    private string _keyFilePath = null!;
    private string _pendingPath = null!;
    private Signer _funder = null!;

    [SetUp]
    public void SetUp()
    {
        _workDir = TempPath.GetTempDirectory();
        Directory.CreateDirectory(_workDir.Path);
        _keyFilePath = Path.Combine(_workDir.Path, "keys.txt");
        _pendingPath = _keyFilePath + ".pending";

        using PrivateKeyGenerator generator = new();
        _funder = new Signer(ChainId, generator.Generate(), LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _workDir.Dispose();

    [TestCase(OneGwei, 1, TestName = "DistributeFunds_OnSuccessfulRun_WithExplicitMaxFee")]
    [TestCase(0UL, 1 + 3, TestName = "DistributeFunds_OnSuccessfulRun_WithMaxFeeZero_RefetchesGasPricePerKey")]
    public async Task DistributeFunds_OnSuccessfulRun_TargetFileContainsAllGeneratedKeys(ulong maxFee, int expectedGasPriceCalls)
    {
        IJsonRpcClient rpcClient = BuildClientReturningOkForEverySend();
        FundsDistributor distributor = new(rpcClient, ChainId, _keyFilePath, LimboLogs.Instance);

        await distributor.DistributeFunds(_funder, keysToMake: 3, maxFee: maxFee, maxPriorityFee: OneGwei);

        Assert.That(File.Exists(_pendingPath), Is.False);
        string[] lines = File.ReadAllLines(_keyFilePath);
        Assert.That(lines.Length, Is.EqualTo(3));
        foreach (string line in lines)
            Assert.That(line.Length, Is.EqualTo(66));
        await rpcClient.Received(expectedGasPriceCalls).Post<string>("eth_gasPrice", Arg.Any<object?[]>());
    }

    [Test]
    public async Task DistributeFunds_OnSuccessfulRun_OverwritesPreExistingKeyFileAtomically()
    {
        File.WriteAllText(_keyFilePath, "0xdeadbeef-stale-from-prior-run\n");

        FundsDistributor distributor = new(BuildClientReturningOkForEverySend(), ChainId, _keyFilePath, LimboLogs.Instance);
        await distributor.DistributeFunds(_funder, keysToMake: 2, maxFee: OneGwei, maxPriorityFee: OneGwei);

        string[] lines = File.ReadAllLines(_keyFilePath);
        Assert.That(lines.Length, Is.EqualTo(2));
        Assert.That(lines[0].StartsWith("0xdeadbeef"), Is.False);
    }

    [Test]
    public void DistributeFunds_WhenSendFailsOnSecondKey_OriginalKeyFileUntouched()
    {
        const string originalContents = "0xdeadbeef-key-from-prior-distribute\n";
        File.WriteAllText(_keyFilePath, originalContents);

        FundsDistributor distributor = new(BuildClientThatFailsOnNthSend(failOnSendIndex: 2), ChainId, _keyFilePath, LimboLogs.Instance);

        Assert.ThrowsAsync<RpcException>(async () =>
            await distributor.DistributeFunds(_funder, keysToMake: 3, maxFee: OneGwei, maxPriorityFee: OneGwei));

        Assert.That(File.ReadAllText(_keyFilePath), Is.EqualTo(originalContents));
    }

    [Test]
    public void DistributeFunds_WhenSendFailsOnSecondKey_PendingFileContainsBothGeneratedKeysWritten()
    {
        FundsDistributor distributor = new(BuildClientThatFailsOnNthSend(failOnSendIndex: 2), ChainId, _keyFilePath, LimboLogs.Instance);

        Assert.ThrowsAsync<RpcException>(async () =>
            await distributor.DistributeFunds(_funder, keysToMake: 3, maxFee: OneGwei, maxPriorityFee: OneGwei));

        string[] lines = File.ReadAllLines(_pendingPath);
        Assert.That(lines.Length, Is.EqualTo(2));
    }

    [Test]
    public void DistributeFunds_WhenPendingFileFromPriorRunExists_ThrowsBeforeAnyKeyOrTxIsTouched()
    {
        File.WriteAllText(_pendingPath, "0xrecovery-candidate\n");
        const string originalContents = "0xexisting\n";
        File.WriteAllText(_keyFilePath, originalContents);

        IJsonRpcClient rpcClient = BuildClientReturningOkForEverySend();
        FundsDistributor distributor = new(rpcClient, ChainId, _keyFilePath, LimboLogs.Instance);

        Assert.ThrowsAsync<IOException>(async () =>
            await distributor.DistributeFunds(_funder, keysToMake: 1, maxFee: OneGwei, maxPriorityFee: OneGwei));

        Assert.That(File.ReadAllText(_pendingPath), Is.EqualTo("0xrecovery-candidate\n"));
        Assert.That(File.ReadAllText(_keyFilePath), Is.EqualTo(originalContents));
        rpcClient.DidNotReceive().Post<string>(Arg.Any<string>(), Arg.Any<object?[]>());
    }

    private static IJsonRpcClient BuildClientReturningOkForEverySend()
    {
        IJsonRpcClient rpcClient = BuildBaseClient();
        rpcClient.Post<string>("eth_sendRawTransaction", Arg.Any<object?[]>()).Returns(OkTxHashHex);
        return rpcClient;
    }

    private static IJsonRpcClient BuildClientThatFailsOnNthSend(int failOnSendIndex)
    {
        IJsonRpcClient rpcClient = BuildBaseClient();
        int sendCallCount = 0;
        rpcClient.Post<string>("eth_sendRawTransaction", Arg.Any<object?[]>())
            .Returns(_ => ++sendCallCount >= failOnSendIndex ? null : OkTxHashHex);
        return rpcClient;
    }

    private static IJsonRpcClient BuildBaseClient()
    {
        IJsonRpcClient rpcClient = Substitute.For<IJsonRpcClient>();
        rpcClient.Post<string>("eth_getBalance", Arg.Any<object?[]>()).Returns(OneEtherHex);
        rpcClient.Post<string>("eth_getTransactionCount", Arg.Any<object?[]>()).Returns(ZeroNonceHex);
        rpcClient.Post<string>("eth_gasPrice", Arg.Any<object?[]>()).Returns(OneGweiHex);
        rpcClient.Post<string>("eth_maxPriorityFeePerGas", Arg.Any<object?[]>()).Returns(OneGweiHex);
        return rpcClient;
    }
}
