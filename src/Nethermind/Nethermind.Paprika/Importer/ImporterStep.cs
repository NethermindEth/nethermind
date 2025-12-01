// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Trie;
using IDb = Paprika.IDb;

namespace Nethermind.Paprika.Importer;

[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ImporterStep(
    IBlockTree blockTree,
    global::Paprika.Chain.Blockchain paprikaBlockchain,
    [KeyFilter(DbNames.State)] INodeStorage stateNodeStorage,
    IProcessExitSource exitSource,
    IDisposableStack disposableStack,
    IDb db,
    ILogManager logManager): IStep
{
    ILogger _logger = logManager.GetClassLogger<ImporterStep>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("execute start");
        new NethermindKestrelMetricServer(9999).Start();
        disposableStack.Push(Prometheus.MeterAdapter.StartListening());

        Hash256 rootHash;
        using (var readOnly = paprikaBlockchain.StartReadOnlyLatestFromDb())
        {
            rootHash = readOnly.Hash.ToNethHash();
        }

        if (rootHash != Hash256.Zero)
        {
            _logger.Warn($"Import skipped as has existing root hash. {rootHash}");
            return;
        }

        BlockHeader? head = blockTree.Head?.Header;
        if (head is null)
        {
            _logger.Warn($"Import skipped as head is null. {rootHash}");
            return;
        }

        _logger.Info($"Starting paprika import from head {head.ToString(BlockHeader.Format.Short)}. StateRoot: {head.StateRoot}");

        // LMDB
        // db.ForceSync();
        var visitor = new PaprikaCopyingVisitor(paprikaBlockchain, 50000, false, logManager);

        var trieStore = new RawTrieStore(stateNodeStorage);
        PatriciaTree trie = new PatriciaTree(trieStore, logManager);
        trie.RootHash = head.StateRoot!;

        var visit = Task.Run(() =>
        {
            trie.Accept(visitor, trie.RootHash, new VisitingOptions
            {
                MaxDegreeOfParallelism = 8,
                //FullScanMemoryBudget = 1L * 1024 * 1024 * 1024
            });

            visitor.Finish();
        });

        var copy = visitor.Copy();
        await Task.WhenAll(visit, copy);

        var finalRootHash = await copy;
        var rootHashNeth = finalRootHash.ToNethHash();

        if (rootHashNeth != head.StateRoot)
        {
            throw new Exception($"Import failed. Root hash mismatched. Expected {head.StateRoot}, got {rootHashNeth}");
        }

        db.Flush();

        _logger.Info("Imported statedb to paprika. Exiting....");
        exitSource.Exit(0);
    }
}
