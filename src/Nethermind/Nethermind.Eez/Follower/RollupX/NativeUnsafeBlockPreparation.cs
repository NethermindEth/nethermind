// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Logging;

namespace Nethermind.Eez.Follower.RollupX;

internal sealed class NativeUnsafeBlockPreparation(
    NativeCanonicalHistory history,
    NativeParentHistory parents,
    EezChainRules rules,
    ILogManager logManager) : IUnsafeBlockPreparation
{
    private readonly NativeHistorySchedule _schedule = new(rules.Schedule!.GenesisTimestamp, rules.Schedule.BlockIntervalSeconds,
        rules.Schedule.SyncIntervalBlocks, rules.NativeHistoryOffset);

    private readonly ILogger _logger = logManager.GetClassLogger<NativeUnsafeBlockPreparation>();
    private NativeL1Header? _lastCanonical;

    public async ValueTask<UnsafeBlockPreparationResult> Prepare(BlockHeader parent, Block block, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        NativeL1Header? checkedEndpoint = null;
        if (_lastCanonical is { } previous)
        {
            checkedEndpoint = await history.ReadCanonical(previous.Number, token);
            if (checkedEndpoint.Hash != previous.Hash)
            {
                _lastCanonical = null;
                history.Invalidate();
                return UnsafeBlockPreparationResult.ParentReorganized;
            }
        }
        NativeHistoryState state = parents.ReadParent(parent);
        ulong last = state.Endpoint;
        NativeHistoryTransaction? historyImport = null;
        for (int index = 0; index < block.Transactions.Length; index++)
        {
            if (block.Transactions[index] is not NativeHistoryTransaction import) continue;
            if (index != 0) throw new EezFollowerException("Unsafe history import is not transaction zero.");
            historyImport = import;
            if (import.Envelope.HasExplicitBlocks != rules.NativeFormat.UsesProxyHistory())
                throw new EezFollowerException("Unsafe history envelope differs from the configured native profile.");
            if (import.Envelope.HasExplicitBlocks)
            {
                foreach (NativeHistoryBlock item in import.Envelope.Blocks)
                    if (item.Number > last) last = item.Number;
            }
            else
            {
                ulong count = (ulong)import.Envelope.Hashes.Length;
                if (last > ulong.MaxValue - count)
                    throw new EezFollowerException("Unsafe history endpoint overflows.");
                last += count;
            }
        }

        if (last - state.Endpoint >= NativeL1HeaderChain.MaxHeaders)
            throw new EezFollowerException("Unsafe history exceeds the canonical preparation limit.");
        NativeL1Header endpoint = checkedEndpoint is not null && checkedEndpoint.Number == last
            ? checkedEndpoint : await history.ReadCanonical(last, token);
        NativeL1HeaderChain? prepared = history.TryRead();
        if (prepared is null || prepared.FirstNumber > state.Endpoint || prepared.Endpoint.Number < last || prepared.At(last).Hash != endpoint.Hash)
        {
            L1Anchor anchor = new(endpoint.Number, new Hash256(endpoint.Hash), endpoint.Timestamp, new Hash256(endpoint.PrevRandao))
            { ParentHash = new Hash256(endpoint.ParentHash) };
            prepared = await history.Prepare(state.Endpoint, anchor, token);
        }
        if (prepared.At(state.Endpoint).Hash != state.EndpointHash)
            return UnsafeBlockPreparationResult.ParentReorganized;
        try
        {
            NativeHistoryExecution execution = _schedule.VerifyContractTransaction(block.Timestamp, in state, prepared,
                historyImport?.Envelope, rules.NativeFormat.UsesProxyHistory());
            if (block.MixHash?.ValueHash256 != execution.PrevRandao)
                return UnsafeBlockPreparationResult.SourceNotCanonical;
            if (_lastCanonical is null || endpoint.Number > _lastCanonical.Number) _lastCanonical = endpoint;
            return UnsafeBlockPreparationResult.Ready;
        }
        catch (EezSettlementException exception)
        {
            if (_logger.IsDebug) _logger.Debug($"Unsafe block {block.Hash} differs from canonical L1 history: {exception}");
            return UnsafeBlockPreparationResult.SourceNotCanonical;
        }
    }
}
