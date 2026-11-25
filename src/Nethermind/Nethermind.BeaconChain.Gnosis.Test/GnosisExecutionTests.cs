// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Reflection;
using System.Text.Json;
using Autofac;
using Nethermind.BeaconChain.Spec;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.LightClient;
using Nethermind.LightClient.Rpc;
using Nethermind.LightClient.Test;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Synchronization;

namespace Nethermind.BeaconChain.Gnosis.Test;

public class GnosisExecutionTests
{
    [OneTimeSetUp]
    public void SelectPreset() => BeaconPresetSelection.Initialize(BlockchainIds.Gnosis);

    [Test]
    public async Task Genesis_and_discovery_use_the_gnosis_execution_network()
    {
        await using ExecutionPeerTransport transport = new("gnosis", LimboLogs.Instance);
        IContainer services = Services(transport);
        ISyncServer server = services.Resolve<ISyncServer>();
        INetworkConfig network = services.Resolve<INetworkConfig>();
        IDiscoveryConfig discovery = services.Resolve<IDiscoveryConfig>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(server.Genesis, Is.SameAs(server.Head));
            Assert.That(server.Genesis, Is.TypeOf<AuRaBlockHeader>());
            Assert.That(server.Genesis.CalculateHash(), Is.EqualTo(KnownHashes.GnosisGenesis));
            Assert.That(server.NetworkId, Is.EqualTo(100));
            Assert.That(transport.SpecProvider.ChainId, Is.EqualTo(100));
            Assert.That(discovery.DiscoveryVersion, Is.EqualTo(DiscoveryVersion.All));
            Assert.That(discovery.UseDefaultDiscv5Bootnodes, Is.False);
            Assert.That(network.DiscoveryDns, Is.Empty);
            Assert.That(network.Bootnodes, Is.Not.Empty);
            Assert.That(services.Resolve<NodesLoaderOptions>().LoadBootnodesAsPeerCandidates, Is.True);
        }
    }

    [Test]
    public async Task Registered_header_decoder_supports_both_gnosis_seal_formats([Values] bool postMerge)
    {
        await using ExecutionPeerTransport transport = new("gnosis", LimboLogs.Instance,
            new VerifiedHead(0, 0, KnownHashes.GnosisGenesis, Hash256.Zero));
        IContainer services = Services(transport);
        IHeaderDecoder decoder = services.Resolve<IHeaderDecoder>();
        BlockHeader original = postMerge ? CallHeader(new VerifiedHead(1, 50_000_000,
            Keccak.Compute("gnosis post-merge header"), Keccak.EmptyTreeHash)) : services.Resolve<ISyncServer>().Genesis;
        BlockHeader decoded = decoder.DecodeCompleteNotNull(decoder.Encode(original));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoder, Is.TypeOf<AuRaHeaderDecoder>());
            Assert.That(decoded, Is.TypeOf(postMerge ? typeof(BlockHeader) : typeof(AuRaBlockHeader)));
            Assert.That(decoded.CalculateHash(), Is.EqualTo(original.CalculateHash()));
            Assert.That(decoded.Number, Is.EqualTo(original.Number));
        }
    }

    [TestCase(0x46, 100UL)]
    [TestCase(0x4a, 1_000_000_000UL)]
    public async Task Calls_and_gas_estimation_execute_with_the_gnosis_spec(byte opcode, ulong expected)
    {
        await using ExecutionPeerTransport transport = new("gnosis", LimboLogs.Instance,
            new VerifiedHead(0, 0, KnownHashes.GnosisGenesis, Hash256.Zero));
        VerifiedHead head = new(1, 50_000_000, Keccak.Compute("gnosis call header"), Keccak.EmptyTreeHash);
        BlockHeader header = CallHeader(head);
        Address contract = new("0x1234567890123456789012345678901234567890");
        byte[] code = [opcode, 0x60, 0x00, 0x52, 0x60, 0x20, 0x60, 0x00, 0xf3];
        Account account = new(1, 0, Keccak.EmptyTreeHash, Keccak.Compute(code));
        ExecutionSource source = new(new Block(header, [], []), contract, account, code);
        using EvmExecutor calls = new(source, transport.SpecProvider, LimboLogs.Instance);
        using RpcBackend rpc = new(source, () => head, transport.SpecProvider.ChainId, calls, transport.SpecProvider);
        JsonElement parameters = JsonSerializer.SerializeToElement(new object[] { new { to = contract.ToString() }, "finalized" });

        object result = await rpc.InvokeAsync("eth_call", parameters, CancellationToken.None);
        object estimate = await rpc.InvokeAsync("eth_estimateGas", parameters, CancellationToken.None);
        object chainId = await rpc.InvokeAsync("eth_chainId", JsonSerializer.SerializeToElement(Array.Empty<object>()), CancellationToken.None);
        IReleaseSpec spec = transport.SpecProvider.GetSpec(header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chainId, Is.EqualTo("0x64"));
            Assert.That(result, Is.EqualTo("0x" + expected.ToString("x64")));
            Assert.That(Convert.ToUInt64(((string)estimate)[2..], 16), Is.InRange(21_000, 30_000));
            Assert.That(spec.FeeCollector, Is.EqualTo(GnosisSpecProvider.FeeCollector));
            Assert.That(spec.BlobBaseFeeUpdateFraction, Is.EqualTo(1_112_826));
        }
    }

    [Test]
    public async Task Premerge_headers_survive_a_persistent_history_reopen()
    {
        await using ExecutionPeerTransport transport = new("gnosis", LimboLogs.Instance);
        IContainer services = Services(transport);
        IHeaderDecoder decoder = services.Resolve<IHeaderDecoder>();
        BlockHeader[] headers = new BlockHeader[3];
        headers[0] = services.Resolve<ISyncServer>().Genesis;
        for (int i = 1; i < headers.Length; i++)
        {
            AuRaBlockHeader header = new(headers[i - 1].Hash!, Keccak.OfAnEmptySequenceRlp, Address.Zero,
                (UInt256)1, (ulong)i, 30_000_000, (ulong)i * 5, [])
            {
                AuRaStep = (ulong)i,
                AuRaSignature = new byte[65],
                StateRoot = Keccak.Compute("gnosis state " + i),
            };
            header.Hash = new Hash256(header.CalculateHash());
            headers[i] = header;
        }
        VerifiedHead anchor = new(2, 2, headers[2].Hash!, headers[2].StateRoot!);
        string directory = Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : Path.GetTempPath(),
            "gnosis-log-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (FinalizedHeaderHistory history = Open(allowFetch: true))
                Assert.That((await history.GetHeadersAsync(anchor, 0, 1, CancellationToken.None)).Select(header => header.Hash),
                    Is.EqualTo(headers.Take(2).Select(header => header.Hash)));

            await using FinalizedHeaderHistory resumed = Open(allowFetch: false);
            BlockHeader[] restored = await resumed.GetHeadersAsync(anchor, 0, 1, CancellationToken.None);
            Assert.That(restored, Has.Length.EqualTo(2));
            Assert.That(restored, Is.All.TypeOf<AuRaBlockHeader>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(restored.Select(header => header.Hash), Is.EqualTo(headers.Take(2).Select(header => header.Hash)));
                Assert.That(((AuRaBlockHeader)restored[1]).AuRaStep, Is.EqualTo(1));
                Assert.That(((AuRaBlockHeader)restored[1]).AuRaSignature, Is.EqualTo(new byte[65]));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

        FinalizedHeaderHistory Open(bool allowFetch)
        {
            IDb db = FinalizedHeaderHistory.OpenDb(directory, LimboLogs.Instance);
            try
            {
                return new(db, KnownHashes.GnosisGenesis, decoder, (_, _) => Task.FromResult(headers[2]),
                    (cursor, count, _) => allowFetch
                        ? Task.FromResult(headers.AsSpan(checked((int)cursor.Number - count), count).ToArray())
                        : throw new AssertionException("Persisted AuRa headers must not be downloaded again."),
                    LimboLogs.Instance.GetClassLogger<FinalizedHeaderHistory>(), capacity: 128, directory: directory);
            }
            catch { db.Dispose(); throw; }
        }
    }

    [Test]
    public async Task Premerge_log_header_bounds_use_the_aura_wire_size([Values(-1, 0, 1)] int overLimit)
    {
        await using ExecutionPeerTransport transport = new("gnosis", LimboLogs.Instance,
            new VerifiedHead(0, 0, KnownHashes.GnosisGenesis, Hash256.Zero));
        IContainer services = Services(transport);
        IHeaderDecoder decoder = services.Resolve<IHeaderDecoder>();
        AuRaBlockHeader header = new(KnownHashes.GnosisGenesis, Keccak.OfAnEmptySequenceRlp,
            Address.Zero, (UInt256)1, 1, 30_000_000, 5, new byte[1500])
        {
            AuRaStep = 1,
            AuRaSignature = new byte[65],
        };
        int overhead = decoder.GetLength(header, RlpBehaviors.None) - header.ExtraData.Length;
        header.ExtraData = new byte[FinalizedHeaderHistory.MaxHeaderBytes + overLimit - overhead];
        header.Hash = new Hash256(header.CalculateHash());
        Assert.That(decoder.GetLength(header, RlpBehaviors.None), Is.EqualTo(FinalizedHeaderHistory.MaxHeaderBytes + overLimit));

        if (overLimit > 0)
            Assert.That(() => LogHeaderChain.Verify(header, 1, header.Hash!), Throws.TypeOf<InvalidDataException>());
        else
            Assert.That(() => LogHeaderChain.Verify(header, 1, header.Hash!), Throws.Nothing);
    }

    internal static BlockHeader CallHeader(VerifiedHead head) => new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp,
        Address.Zero, UInt256.Zero, head.Number, 30_000_000, 1_800_000_000, [])
    {
        Hash = head.BlockHash,
        StateRoot = head.StateRoot,
        BaseFeePerGas = 1,
        MixHash = Keccak.Zero,
        BlobGasUsed = 0,
        ExcessBlobGas = 0,
        IsPostMerge = true,
    };

    internal static IContainer Services(ExecutionPeerTransport transport) =>
        (IContainer)typeof(ExecutionPeerTransport).GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;

    internal sealed class ExecutionSource(Block block, Address? contract = null, Account? account = null, byte[]? code = null)
        : IExecutionStateSource
    {
        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
        {
            RequireHead(head, cancellationToken);
            return Task.FromResult(block.Header);
        }

        public Task<Block> GetBlockAsync(BlockHeader header, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (header.Hash != block.Hash || header.Number != block.Number || header.StateRoot != block.StateRoot)
                throw new AssertionException("Unexpected execution block request.");
            return Task.FromResult(block);
        }

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
        {
            RequireHead(head, cancellationToken);
            if (contract is null || account is null) throw new NotSupportedException();
            return Task.FromResult(address == contract ? account : Account.TotallyEmpty);
        }

        public Task<byte[]> GetCodeAsync(Account requested, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (account is null || code is null || requested.CodeHash != account.CodeHash)
                throw new AssertionException("Unexpected execution bytecode request.");
            return Task.FromResult(code);
        }

        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private void RequireHead(VerifiedHead head, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (head.BlockHash != block.Hash || head.Number != block.Number || head.StateRoot != block.StateRoot)
                throw new AssertionException("Unexpected execution head request.");
        }
    }
}
