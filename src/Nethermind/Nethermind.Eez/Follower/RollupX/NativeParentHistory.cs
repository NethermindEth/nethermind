// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Eez.Follower.RollupX;

internal sealed class NativeParentHistory(IStateReader state, EezChainRules rules)
{
    private readonly EezChainRules _rules = rules;
    private readonly RollupXSchedule _schedule = rules.Schedule!;
    private readonly NativeHistorySchedule _history = new(rules.Schedule!.GenesisTimestamp, rules.Schedule.BlockIntervalSeconds,
        rules.Schedule.SyncIntervalBlocks, rules.NativeHistoryOffset);

    internal NativeHistoryState ReadParent(BlockHeader parent)
    {
        if (!state.TryGetAccount(parent, EezConstants.L1HistoryAddress, out AccountStruct account) ||
            (_rules.NativeFormat.UsesProxyHistory() ? account.Nonce < 2 : account.Nonce != 1) ||
            account.CodeHash != NativeHistoryBlockExecutor.GetRuntimeHash(_rules.NativeFormat))
            throw NativeL1Header.Invalid("parent history account differs from the native profile");
        if (_rules.NativeFormat.UsesProxyHistory())
        {
            ulong storedNumber = Scalar(parent, 1);
            state.GetStorage(parent, EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(storedNumber), out UInt256 storedHash);
            return NativeProxyHistory.ResolveState(storedNumber, storedHash.ToValueHash(), _schedule);
        }
        ulong first = Scalar(parent, 1), endpoint = Scalar(parent, 2), cutoff = Scalar(parent, 3);
        if (first != _rules.Schedule!.L1Reference.Number || Scalar(parent, 4) != _history.Interval ||
            Scalar(parent, 5) != _history.Delay || Scalar(parent, 6) != NativeHistoryCall.MaxHashes)
            throw NativeL1Header.Invalid("parent history storage differs from the native profile");
        state.GetStorage(parent, EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(endpoint), out UInt256 hash);
        return new NativeHistoryState(first, endpoint, hash.ToValueHash(), cutoff);
    }

    private ulong Scalar(BlockHeader parent, ulong slot)
    {
        state.GetStorage(parent, EezConstants.L1HistoryAddress, (UInt256)slot, out UInt256 value);
        return value > ulong.MaxValue ? throw NativeL1Header.Invalid("parent history scalar exceeds uint64") : (ulong)value;
    }

}
