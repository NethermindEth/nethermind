// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Arkiv.Native;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Arkiv.Execution;

/// <summary>
/// A block being built natively on the store's head (<c>arkiv_ffi::build</c>): transactions applied one at a time, each
/// taken or refused by Arkiv's executor, then sealed for the root the header must carry. Never committed: the block
/// commits when it is imported like any other.
/// </summary>
public sealed class ArkivBuild : IDisposable
{
    private ulong _session;
    private ulong _cumulativeGas;

    internal ArkivBuild(ulong session) => _session = session;

    /// <summary>Applies <paramref name="tx"/>; its receipt and the gas it spent, or null with the reason it was refused.</summary>
    public (ArkivReceipt? Receipt, ulong Spent, string? Refused) Apply(Transaction tx)
    {
        byte[] encoded = Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping);
        try
        {
            ArkivReceipt receipt = ArkivBlockResult.DecodeReceipt(ArkivNative.BuildApply(_session, encoded, tx.SenderAddress!.Bytes));
            ulong spent = receipt.CumulativeGasUsed - _cumulativeGas;
            _cumulativeGas = receipt.CumulativeGasUsed;
            return (receipt, spent, null);
        }
        catch (ArkivNativeException e) when (e.Status == ArkivStatus.InvalidBlock)
        {
            return (null, 0, e.NativeMessage);
        }
    }

    /// <summary>Seals the block: its root, gas and receipts. The build is over after this.</summary>
    public ArkivBlockResult Finish()
    {
        ulong session = _session;
        _session = 0;
        return ArkivBlockResult.Decode(ArkivNative.BuildFinish(session));
    }

    public void Dispose()
    {
        if (_session != 0) ArkivNative.arkiv_build_abort(_session);
        _session = 0;
    }
}
