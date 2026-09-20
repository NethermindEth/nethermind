// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Network;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// The production data availability gate as <see cref="BlockImporter"/> applies it, driven end to
/// end through <see cref="BlockImporter.Import"/> with a genuinely valid signed blob block
/// (<see cref="ImportableBlobBlock"/>): availability is the only check left that can reject it.
/// At the base of this change the importer passed no columns at all to a rule that demands every
/// column, so every blob-carrying block was rejected; the positive case here is what proves the
/// gate now admits the blocks a base-custody node is actually able to verify.
/// </summary>
public class BlockImporterTests
{
    private static readonly Hash256 NodeId = new([.. Enumerable.Repeat((byte)0x42, 32)]);

    /// <summary>A base-custody node: four custody groups, eight sampled columns per slot on mainnet.</summary>
    private static NodeColumnCustody BaseCustody() => new(NodeId, Eip7594DasConstants.CustodyRequirement);

    [Test]
    public void Blob_block_with_every_sampled_column_held_and_verified_imports()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporter importer = CreateImporter(chain, custody, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "eight verified columns out of 128 is exactly what a base-custody node can hold, and must suffice");
            Assert.That(importer.IsKnown(chain.BlockRoot), Is.True);
            Assert.That(custody.SampledColumns, Has.Count.EqualTo(8), "the fixture node is a base-custody node, not a supernode");
        });
    }

    [Test]
    public void Blob_block_missing_one_custody_column_is_rejected()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        ulong missing = custody.CustodyColumns[0];
        Hold(pool, chain, custody.SampledColumns.Where(c => c != missing));
        WarningCapture warnings = new();
        BlockImporter importer = CreateImporter(chain, custody, pool, warnings);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
            Assert.That(importer.IsKnown(chain.BlockRoot), Is.False, "a block whose data is unavailable must not enter fork choice");
            Assert.That(warnings.Warnings, Has.Some.Contains("blob data available"), "rejected for availability, not for some other reason");
        });
    }

    [Test]
    public void Blob_block_missing_a_sampled_but_not_custodied_column_is_rejected()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecarPool pool = new();
        ulong missing = custody.SampledColumns.First(c => !custody.CustodyColumns.Contains(c));
        Hold(pool, chain, custody.SampledColumns.Where(c => c != missing));
        BlockImporter importer = CreateImporter(chain, custody, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid), "custody columns alone are not enough: the per-slot sample must succeed too");
    }

    [Test]
    public void Blob_block_is_rejected_while_the_node_identity_is_unknown()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(c => (ulong)c));
        BlockImporter importer = CreateImporter(chain, custody: null, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid), "a rule that cannot say which columns it needs cannot say a block is available, even holding all 128");
    }

    [Test]
    public void Held_column_that_fails_kzg_verification_does_not_count()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecar tampered = chain.Columns[(int)custody.CustodyColumns[0]];
        byte[] cell = tampered.Column![0].AsSpan().ToArray();
        cell[0] ^= 0xFF;
        tampered.Column[0] = SszBlobCell.FromSpan(cell);
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporter importer = CreateImporter(chain, custody, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid), "holding a column is not availability; the column must verify against the block's commitments");
    }

    [Test]
    public void Held_column_addressed_to_a_different_block_does_not_count()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        // Same index, same commitments, valid proofs: only the header names another block.
        chain.Columns[(int)custody.CustodyColumns[0]].SignedBlockHeader!.Message!.Slot = 999;
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporter importer = CreateImporter(chain, custody, pool);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid));
    }

    [Test]
    public void Block_without_blob_commitments_imports_with_no_columns_and_no_identity()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        BlockImporter importer = CreateImporter(chain, custody: null, new DataColumnSidecarPool());

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "a block with no blobs needs no columns");
    }

    [Test]
    public void Trusted_store_replay_imports_a_blob_block_without_its_columns()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BlockImporter importer = CreateImporter(chain, BaseCustody(), new DataColumnSidecarPool());

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: false);

        Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "the store only holds blocks that already passed the gate, and their columns are not persisted; re-checking would stall every restart");
    }

    [Test]
    public async Task Factory_built_importer_admits_a_blob_block_once_the_columns_of_the_discovery_identity_are_held()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        await using BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, chain.Spec, store, new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        // Resolves the identity and local custody exactly as Start does, without binding a socket.
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        NodeColumnCustody custody = new DiscoveryNodeCustodySource(discovery).Current!;
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, custody.SampledColumns);
        BlockImporterFactory factory = new(chain.Spec, store, chain.Pubkeys, new ValidPayloadEngine(), new BeaconChainConfig(), LimboLogs.Instance, pool, discovery);
        IBlockImporter importer = factory.Create(chain.AnchorState, chain.AnchorBlock, chain.AnchorRoot);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.Multiple(() =>
        {
            Assert.That(custody.CustodyGroupCount, Is.EqualTo(discovery.LocalCustody.CustodyGroupCount), "the importer demands the custody discovery advertises");
            Assert.That(custody.SampledColumns, Has.Count.EqualTo(8));
            Assert.That(result, Is.EqualTo(BlockImportResult.Imported), "the production wiring, end to end: eight held columns of this node's real identity admit the block");
        });
    }

    [Test]
    public void Factory_built_importer_applies_the_custody_rule_and_fails_closed_without_discovery()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        DataColumnSidecarPool pool = new();
        Hold(pool, chain, Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(c => (ulong)c));
        BlockImporterFactory factory = new(chain.Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), chain.Pubkeys, new ValidPayloadEngine(), new BeaconChainConfig(), LimboLogs.Instance, pool);
        IBlockImporter importer = factory.Create(chain.AnchorState, chain.AnchorBlock, chain.AnchorRoot);

        BlockImportResult result = importer.Import(chain.Block, chain.BlockRoot, verifySignatures: true);

        Assert.That(result, Is.EqualTo(BlockImportResult.Invalid), "no discovery means no node id, so the custody rule has no columns to demand and must refuse rather than pass");
    }

    private static void Hold(DataColumnSidecarPool pool, ImportableBlobBlock chain, IEnumerable<ulong> columns)
    {
        foreach (ulong column in columns)
        {
            pool.Add(chain.BlockRoot, chain.Block.Message!.Slot, chain.Columns[(int)column]);
        }
    }

    private static BlockImporter CreateImporter(ImportableBlobBlock chain, NodeColumnCustody? custody, DataColumnSidecarPool pool, WarningCapture? warnings = null) =>
        new(
            chain.Spec,
            new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            chain.Pubkeys,
            new ValidPayloadEngine(),
            new BeaconChainConfig(),
            warnings is null ? LimboLogs.Instance : new OneLoggerLogManager(new ILogger(warnings)),
            new CustodySamplingAvailability(new FixedCustodySource(custody), new DataColumnPoolSource(pool)),
            chain.AnchorState,
            chain.AnchorBlock,
            chain.AnchorRoot);

    private sealed class FixedCustodySource(NodeColumnCustody? custody) : INodeColumnCustodySource
    {
        public NodeColumnCustody? Current => custody;
    }

    private sealed class FixedIPResolver(IPAddress ip) : IIPResolver
    {
        public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default) =>
            new(new IIPResolver.NethermindIp(ip, ip));
    }

    private sealed class ValidPayloadEngine : IEngineDriver
    {
        public SignedBeaconBlock? CurrentBlock { get; set; }

        public PayloadStatusV1? LastNewPayloadStatus { get; private set; }

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash) =>
            Task.FromResult(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = headExecHash });

        public bool NotifyNewPayload(BeaconBlockBody body)
        {
            LastNewPayloadStatus = new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = body.ExecutionPayload!.BlockHash };
            return true;
        }
    }

    private sealed class WarningCapture : InterfaceLogger
    {
        public List<string> Warnings { get; } = [];

        public bool IsInfo => false;
        public bool IsWarn => true;
        public bool IsDebug => false;
        public bool IsTrace => false;
        public bool IsError => true;

        public void Info(string text) { }
        public void Warn(string text) => Warnings.Add(text);
        public void Debug(string text) { }
        public void Trace(string text) { }
        public void Error(string text, Exception? ex = null) => Warnings.Add(text);
    }
}
