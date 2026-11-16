// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.LightClient;

internal sealed class EezHeadPublisher(IExecutionStateSource settlementSource, IExecutionStateSource rollupSource,
    EezSettlement settlement, Action<VerifiedHead, ulong> publish)
{
    private EezCommittedHead? _current;

    internal EezCommittedHead? Current => Volatile.Read(ref _current);

    internal async Task<bool> UpdateAsync(VerifiedHead settlementHead, CancellationToken cancellationToken)
    {
        EezCommittedHead? current = Current;
        if (current is not null && settlementHead.Number <= current.SettlementHead.Number) return false;

        EezCommittedHead? next = await EezCommitment.ResolveAsync(settlementSource, rollupSource, settlement,
            settlementHead, cancellationToken);
        if (next is null) return false;
        if (current is not null && (next.Head.Number < current.Head.Number
            || next.Head.Number == current.Head.Number && next.Head.BlockHash != current.Head.BlockHash))
            throw new InvalidDataException("Finalized EEZ settlement commitment moved away from the published rollup head.");

        Volatile.Write(ref _current, next);
        publish(next.Head, next.Header.Timestamp);
        return true;
    }
}
