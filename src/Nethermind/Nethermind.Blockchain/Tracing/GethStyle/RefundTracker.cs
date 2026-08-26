// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Blockchain.Tracing.GethStyle;

internal sealed class RefundTracker(long destroyRefund)
{
    private readonly Stack<Checkpoint> _checkpoints = new();
    private JournalSet<Address>? _selfDestructs;

    public long Refund { get; private set; }

    public void Add(long refund) => Refund += refund;

    public void CreditSelfDestruct(Address address)
    {
        if (destroyRefund != 0 && (_selfDestructs ??= new(Address.EqualityComparer)).Add(address))
            Refund += destroyRefund;
    }

    public void TakeSnapshot() => _checkpoints.Push(new(Refund, _selfDestructs?.TakeSnapshot() ?? -1));

    public void CommitSnapshot() => _checkpoints.TryPop(out _);

    public void RestoreSnapshot()
    {
        if (_checkpoints.TryPop(out Checkpoint checkpoint))
        {
            Refund = checkpoint.Refund;
            _selfDestructs?.Restore(checkpoint.SelfDestructs);
        }
    }

    private readonly record struct Checkpoint(long Refund, int SelfDestructs);
}
