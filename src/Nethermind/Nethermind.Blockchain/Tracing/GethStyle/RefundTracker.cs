// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Tracks the EVM refund counter across nested call-frame snapshots.
/// </summary>
/// <param name="destroyRefund">Refund awarded for the first successful legacy self-destruct of an account.</param>
public sealed class RefundTracker(long destroyRefund)
{
    private readonly Stack<Checkpoint> _checkpoints = new();
    private JournalSet<Address>? _selfDestructs;

    /// <summary>Gets the current refund counter.</summary>
    public long Refund { get; private set; }

    /// <summary>Adds a reported refund to the current counter.</summary>
    /// <param name="refund">The refund to add.</param>
    public void Add(long refund) => Refund += refund;

    /// <summary>Credits a legacy self-destruct refund once per account.</summary>
    /// <remarks>
    /// The refund is credited at the opcode boundary because transaction finalization reports it again
    /// after the final trace entry has already sampled the counter.
    /// EIP-3529 sets <c>destroyRefund</c> to zero before EIP-6780 restricts which accounts can be destroyed.
    /// </remarks>
    /// <param name="address">The self-destructed account.</param>
    public void CreditSelfDestruct(Address address)
    {
        if (destroyRefund != 0 && (_selfDestructs ??= new(Address.EqualityComparer)).Add(address))
            Refund += destroyRefund;
    }

    /// <summary>Records the current refund state before entering a call frame.</summary>
    public void TakeSnapshot() => _checkpoints.Push(new(Refund, _selfDestructs?.TakeSnapshot() ?? -1));

    /// <summary>Commits the latest call-frame refund snapshot.</summary>
    public void CommitSnapshot() => _checkpoints.TryPop(out _);

    /// <summary>Restores and removes the latest call-frame refund snapshot.</summary>
    public void RestoreSnapshot()
    {
        if (_checkpoints.TryPop(out Checkpoint checkpoint))
        {
            Refund = checkpoint.Refund;
            _selfDestructs?.Restore(checkpoint.SelfDestructs);
        }
    }

    /// <summary>Clears all accumulated refund state.</summary>
    /// <remarks>Used only by in-assembly tracers that reuse one instance across transactions.</remarks>
    internal void Reset()
    {
        Refund = 0;
        _checkpoints.Clear();
        _selfDestructs?.Clear();
    }

    private readonly record struct Checkpoint(long Refund, int SelfDestructs);
}
