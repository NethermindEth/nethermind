// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>The owned history envelope; execution input is derived without changing its wire fields.</summary>
internal sealed class NativeHistoryTransaction : Transaction
{
    internal NativeHistoryEnvelope Envelope { get; }
    internal bool IsExecutionView { get; private init; }
    internal bool HasWireFields => !IsExecutionView && (byte)Type == NativeHistoryEnvelope.Type
        && ChainId == Envelope.ChainId && SenderAddress == Address.SystemUser && To == EezConstants.L1HistoryAddress
        && GasLimit == EezConstants.L1HistoryTxGasCap && ValueRef.IsZero && Data.IsEmpty && Nonce == 0
        && SourceHash is null && Signature is null && GasPrice.IsZero && DecodedMaxFeePerGas.IsZero
        && MaxFeePerBlobGas is null && AccessList is null && AuthorizationList is null && BlobVersionedHashes is null;

    internal NativeHistoryTransaction(NativeHistoryEnvelope envelope)
    {
        Envelope = envelope;
        Type = (TxType)NativeHistoryEnvelope.Type;
        ChainId = envelope.ChainId;
        SenderAddress = Address.SystemUser;
        To = EezConstants.L1HistoryAddress;
        GasLimit = EezConstants.L1HistoryTxGasCap;
    }

    internal NativeHistoryTransaction ForExecution(ulong cutoff) => new(Envelope)
    {
        Hash = Hash,
        Data = Envelope.HasExplicitBlocks ? NativeHistoryCall.EncodeBlocks(Envelope) : NativeHistoryCall.Encode(cutoff, Envelope.Hashes),
        IsExecutionView = true
    };
}
