// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Arkiv.Native;

namespace Nethermind.Arkiv.Execution;

/// <summary>
/// The block a processing scope is executing, shared by its <see cref="ArkivBlockProcessor"/> (which runs the block
/// natively) and its <see cref="ArkivTransactionProcessor"/> (which hands each transaction its outcome). One per
/// processing scope, like the processors themselves.
/// </summary>
public sealed class ArkivBlockContext
{
    private int _next;

    /// <summary>The executing block's native result, or null outside block processing (calls, estimates).</summary>
    public ArkivBlockResult? Current { get; private set; }

    /// <summary>The block being built, transaction by transaction, or null when not building.</summary>
    public ArkivBuild? Build { get; private set; }

    public void Begin(ArkivBlockResult result)
    {
        Current = result;
        _next = 0;
    }

    public void BeginBuild(ArkivBuild build) => Build = build;

    /// <summary>Ends whatever ran; a build not finished by then is abandoned.</summary>
    public void End()
    {
        Current = null;
        Build?.Dispose();
        Build = null;
    }

    /// <summary>The next transaction's receipt and the cumulative gas before it.</summary>
    public (ArkivReceipt Receipt, ulong GasBefore) TakeNext()
    {
        ArkivBlockResult block = Current!;
        int index = _next++;
        ulong before = index == 0 ? 0 : block.Receipts[index - 1].CumulativeGasUsed;
        return (block.Receipts[index], before);
    }
}
