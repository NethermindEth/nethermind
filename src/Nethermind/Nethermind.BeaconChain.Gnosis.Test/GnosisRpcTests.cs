// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Autofac;
using Nethermind.BeaconChain.Spec;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.LightClient;
using Nethermind.LightClient.Rpc;
using Nethermind.LightClient.Test;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State.Proofs;
using Nethermind.Synchronization;

namespace Nethermind.BeaconChain.Gnosis.Test;

public class GnosisRpcTests
{
    [OneTimeSetUp]
    public void SelectPreset() => BeaconPresetSelection.Initialize(BlockchainIds.Gnosis);

    [Test]
    public async Task Simulate_processes_Gnosis_withdrawals_through_the_configured_contract([Values] bool withdrawalsEnabled)
    {
        VerifiedHead head = new(1, 30_000_000, Keccak.Compute("Gnosis simulation head"), Keccak.EmptyTreeHash);
        await using ExecutionPeerTransport transport = new("gnosis", LimboLogs.Instance, head);
        Address recipient = new("0x1000000000000000000000000000000000000001");
        Address withdrawalContract = new("0x0B98057eA310F4d31F2a452B414647007d1645d9");
        ulong secondsPerSlot = BeaconChainSpec.Gnosis.SecondsPerSlot;
        BlockHeader header = GnosisExecutionTests.CallHeader(head);
        header.Timestamp = GnosisSpecProvider.ShanghaiTimestamp - (withdrawalsEnabled ? secondsPerSlot : 3 * secondsPerSlot);
        header.BlobGasUsed = header.ExcessBlobGas = null;

        // A system call stores amounts[0], starting at ABI offset 0x84. An empty call reads the native balance and the slot.
        byte[] code = [0x36, 0x15, 0x60, 0x0c, 0x57, 0x60, 0x84, 0x35, 0x60, 0x00, 0x55, 0x00, 0x5b,
            0x73, .. recipient.Bytes, 0x31, 0x60, 0x00, 0x52, 0x60, 0x00, 0x54, 0x60, 0x20, 0x52, 0x60, 0x40, 0x60, 0x00, 0xf3];
        Account account = new(1, 0, Keccak.EmptyTreeHash, Keccak.Compute(code));
        GnosisExecutionTests.ExecutionSource source = new(new Block(header, [], []), withdrawalContract, account, code);
        using EvmExecutor calls = new(source, transport.SpecProvider, LimboLogs.Instance, secondsPerSlot: secondsPerSlot,
            withdrawalProcessorFactory: transport.WithdrawalProcessorFactory);
        using RpcBackend rpc = new(source, () => head, BlockchainIds.Gnosis, calls, transport.SpecProvider, () => head);
        object payload = new
        {
            blockStateCalls = new object[]
            {
                new { blockOverrides = new { withdrawals = new[] { new { index = "0x0", validatorIndex = "0x1", address = recipient.ToString(), amount = "0x7" } } } },
                new { calls = new[] { new { to = withdrawalContract.ToString() } } },
            },
        };

        JsonElement blocks = (JsonElement)await rpc.InvokeAsync("eth_simulateV1",
            JsonSerializer.SerializeToElement(new[] { payload }), CancellationToken.None);

        JsonElement observed = blocks[1].GetProperty("calls")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(observed.GetProperty("status").GetString(), Is.EqualTo("0x1"));
            Assert.That(observed.GetProperty("returnData").GetString(), Is.EqualTo("0x" + new string('0', 127) + (withdrawalsEnabled ? "7" : "0")));
        }
    }

    [Test]
    public async Task Blocks_and_subscription_headers_preserve_the_registered_seal_shape(
        [Values("genesis", "aura", "pos")] string era, [Values] bool byHash, [Values] bool fullTransactions)
    {
        await using ExecutionPeerTransport transport = new("gnosis", LimboLogs.Instance);
        IContainer services = GnosisExecutionTests.Services(transport);
        IBlockForRpcFactory factory = services.Resolve<IBlockForRpcFactory>();
        Assert.That(factory, Is.TypeOf<AuRaBlockForRpcFactory>());
        BlockHeader header = era == "genesis" ? services.Resolve<ISyncServer>().Genesis
            : GnosisExecutionTests.CallHeader(new VerifiedHead(1, 50_000_000, Keccak.Zero, Keccak.EmptyTreeHash));
        if (era == "aura")
        {
            AuRaBlockHeader aura = AuRaBlockHeader.UpgradeFrom(header);
            aura.Number = 1;
            aura.ParentHash = KnownHashes.GnosisGenesis;
            aura.Timestamp = 1_638_993_345;
            aura.Difficulty = UInt256.One;
            aura.IsPostMerge = false;
            aura.MixHash = null;
            aura.AuRaStep = 17;
            aura.AuRaSignature = Enumerable.Range(0, 65).Select(static value => (byte)value).ToArray();
            aura.Author = new Address("0x1234567890123456789012345678901234567890");
            header = aura;
        }
        using PrivateKey key = new("0x0000000000000000000000000000000000000000000000000000000000000001");
        Transaction[] transactions = [];
        if (era != "genesis")
        {
            Transaction transaction = new() { Nonce = 7, GasLimit = 21_000, GasPrice = 2, To = key.Address, Value = 3 };
            new EthereumEcdsa(100).Sign(key, transaction, isEip155Enabled: true);
            transaction.Hash = transaction.CalculateHash();
            transaction.SenderAddress = null;
            transactions = [transaction];
            header.TxRoot = TxTrie.CalculateRoot(transactions);
            header.Hash = header.CalculateHash();
        }
        Block block = new(header, transactions, []);
        VerifiedHead head = new(1, header.Number, header.Hash!, header.StateRoot!);
        GnosisExecutionTests.ExecutionSource source = new(block);
        using RpcBackend rpc = new(source, () => head, 100, specProvider: transport.SpecProvider,
            getLatestHead: () => head, blockForRpcFactory: factory);
        string method = byHash ? "eth_getBlockByHash" : "eth_getBlockByNumber";
        string selector = byHash ? header.Hash!.ToString() : $"0x{header.Number:x}";

        JsonElement result = (JsonElement)await rpc.InvokeAsync(method,
            JsonSerializer.SerializeToElement(new object[] { selector, fullTransactions }), CancellationToken.None);
        JsonElement notification = JsonElement.Parse(rpc.GetHeader(header).Utf8Json);

        foreach (JsonElement projected in new[] { result, notification })
        {
            Assert.That(projected.TryGetProperty("nonce", out JsonElement nonce), Is.True);
            if (header is AuRaBlockHeader aura)
            {
                bool hasStep = projected.TryGetProperty("step", out JsonElement step);
                bool hasSignature = projected.TryGetProperty("signature", out JsonElement signature);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(hasStep, Is.True);
                    Assert.That(hasSignature, Is.True);
                    Assert.That(step.ValueKind, Is.EqualTo(JsonValueKind.Number));
                    Assert.That(signature.ValueKind, Is.EqualTo(JsonValueKind.String));
                }
                string? author = aura.Author is null ? null : projected.GetProperty("author").GetString();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(step.GetUInt64(), Is.EqualTo(aura.AuRaStep));
                    Assert.That(signature.GetString(), Is.EqualTo("0x" + Convert.ToHexString(aura.AuRaSignature!).ToLowerInvariant()));
                    Assert.That(nonce.ValueKind, Is.EqualTo(JsonValueKind.Null));
                    Assert.That(projected.TryGetProperty("mixHash", out _), Is.False);
                    if (aura.Author is not null)
                        Assert.That(author, Is.EqualTo(aura.Author.ToString()));
                }
            }
            else
            {
                Assert.That(nonce.ValueKind, Is.EqualTo(JsonValueKind.String));
                string? mixHash = projected.GetProperty("mixHash").GetString();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(nonce.GetString(), Is.EqualTo("0x0000000000000000"));
                    Assert.That(mixHash, Is.EqualTo(header.MixHash!.ToString()));
                    Assert.That(projected.TryGetProperty("step", out _), Is.False);
                    Assert.That(projected.TryGetProperty("signature", out _), Is.False);
                    Assert.That(projected.TryGetProperty("baseFeePerGas", out _), Is.True);
                    Assert.That(projected.TryGetProperty("blobGasUsed", out _), Is.True);
                }
            }
        }
        JsonElement projectedTransactions = result.GetProperty("transactions");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(projectedTransactions.GetArrayLength(), Is.EqualTo(transactions.Length));
            Assert.That(notification.TryGetProperty("transactions", out _), Is.False);
        }
        if (transactions.Length != 0)
        {
            JsonElement transaction = projectedTransactions[0];
            Assert.That(transaction.ValueKind, Is.EqualTo(fullTransactions ? JsonValueKind.Object : JsonValueKind.String));
            if (fullTransactions)
            {
                string? hash = transaction.GetProperty("hash").GetString();
                string? sender = transaction.GetProperty("from").GetString();
                string? chainId = transaction.GetProperty("chainId").GetString();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(hash, Is.EqualTo(transactions[0].Hash!.ToString()));
                    Assert.That(sender, Is.EqualTo(key.Address.ToString()));
                    Assert.That(chainId, Is.EqualTo("0x64"));
                }
            }
            else Assert.That(transaction.GetString(), Is.EqualTo(transactions[0].Hash!.ToString()));
        }
    }
}
