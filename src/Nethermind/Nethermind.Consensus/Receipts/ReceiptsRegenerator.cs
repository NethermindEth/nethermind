// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Consensus.Receipts;

/// <summary>
/// Rebuilds a block's receipts by re-executing it over the state of its parent, accepting the result only when it
/// reproduces the header's receipts root.
/// </summary>
/// <remarks>
/// Lets an archive drop stored receipt bodies and answer receipt queries out of state history instead. On every shipped
/// chainspec the root check is exhaustive — the receipts trie covers every consensus field of every receipt
/// (only an unshipped validateReceiptsTransition=false spec would relax it) — so a fork-rule gap
/// surfaces as a refused regeneration rather than a wrong answer. Blocks before EIP-658 are refused outright:
/// their receipts carry a post-transaction state root, which a history-backed scope cannot produce.
/// </remarks>
public sealed class ReceiptsRegenerator(
    IShareableOverridableEnvSource<ReceiptsRegenerationEnv> envSource,
    IBlockFinder blockFinder,
    ISpecProvider specProvider,
    IEthereumEcdsa ecdsa,
    IPoSSwitcher poSSwitcher,
    ILogManager logManager)
{
    private readonly ILogger _logger = logManager.GetClassLogger<ReceiptsRegenerator>();

    /// <summary>
    /// Re-executes <paramref name="block"/> and yields its receipts when they reproduce the header's receipts root.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the block predates EIP-658, its parent header is gone, or the regenerated receipts fail
    /// the root check.
    /// </returns>
    public bool TryRegenerate(Block block, [NotNullWhen(true)] out TxReceipt[]? receipts)
    {
        receipts = null;

        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        if (!spec.IsEip658Enabled) return false;

        BlockHeader? parent = blockFinder.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
        if (parent is null) return false;

        foreach (Transaction tx in block.Transactions)
        {
            if (tx.SenderAddress is not null)
            {
                continue;
            }

            if (!ecdsa.TryRecoverAddress(tx, out Address? senderAddress, !spec.ValidateChainId))
            {
                return FailUnrecoverableSender(block, tx);
            }

            tx.SenderAddress = senderAddress;
        }

        // Re-execute on a throwaway copy: block processing writes back the resolved state root, account-change
        // buffers, and generated access list, and this runs on RPC threads against the shared block-tree instance —
        // so it must never see that instance. The parent header is cloned for the same reason (BuildAndOverride
        // assigns the resolved state root into whatever header it is handed).
        BlockHeader isolatedHeader = block.Header.Clone();
        // Storage does not persist the post-merge flag and this path bypasses the recovery step that restores it;
        // without it PREVRANDAO evaluates to the pre-merge difficulty and any transaction reading it regenerates
        // receipts that fail the root check. The switcher owns the classification - a difficulty heuristic would
        // misread chains that repurpose the field, like Taiko's per-block ZK gas.
        isolatedHeader.IsPostMerge = poSSwitcher.IsPostMerge(isolatedHeader);
        Block isolated = new(isolatedHeader, block.Body, block.BlockAccessList);
        try
        {
            using Scope<ReceiptsRegenerationEnv> scope = envSource.BuildAndOverride(parent.Clone());

            // NoValidation is required, not an optimisation: the shared validator is the merge plugin's
            // InvalidBlockInterceptor, which records into the process-wide InvalidChainTracker and could make the
            // Engine API reject valid blocks. The receipts root is checked below instead.
            (Block _, TxReceipt[] regenerated) = scope.Component.BlockProcessor
                .ProcessOne(isolated, ProcessingOptions.ReadOnlyChain | ProcessingOptions.NoValidation,
                    NullBlockTracer.Instance, spec, CancellationToken.None);

            Hash256 regeneratedRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(regenerated, spec, block.Header.ReceiptsRoot);
            if (regeneratedRoot != block.Header.ReceiptsRoot)
            {
                if (_logger.IsError)
                    _logger.Error($"Regenerated receipts for block {block.Number} ({block.Hash}) hash to {regeneratedRoot} " +
                                  $"but the header commits to {block.Header.ReceiptsRoot}. Refusing to serve them.");
                return false;
            }

            receipts = regenerated;
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException
            and not ConcurrencyLimitReachedException
            and not ObjectDisposedException)
        {
            // Missing state history or a pruned trie node means "cannot regenerate", not a failed query. The
            // transient shapes above must escape: swallowing an exhausted env pool into "false" would surface as
            // pruned-history-unavailable — a don't-retry signal — for data the node can serve once load drops.
            if (_logger.IsWarn) _logger.Warn($"Could not regenerate receipts for block {block.Number} ({block.Hash}): {e.Message}");
            return false;
        }
        finally
        {
            // Block processing only fills the account-change buffer on the main processing thread, but release it
            // defensively so a pooled buffer can never outlive this call regardless of where regeneration runs.
            isolated.DisposeAccountChanges();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool FailUnrecoverableSender(Block block, Transaction tx)
    {
        if (_logger.IsWarn) _logger.Warn($"Could not regenerate receipts for block {block.Number} ({block.Hash}): transaction {tx.Hash} sender address is not recoverable.");
        return false;
    }
}
