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
        using VerifiedRpc rpc = new(source, () => head, 100, specProvider: transport.SpecProvider,
            getLatestHead: () => head, blockForRpcFactory: factory);
        string method = byHash ? "eth_getBlockByHash" : "eth_getBlockByNumber";
        string selector = byHash ? header.Hash!.ToString() : $"0x{header.Number:x}";

        JsonElement result = (JsonElement)await rpc.InvokeAsync(method,
            JsonSerializer.SerializeToElement(new object[] { selector, fullTransactions }), CancellationToken.None);
        JsonElement notification = rpc.GetHeader(header);

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
