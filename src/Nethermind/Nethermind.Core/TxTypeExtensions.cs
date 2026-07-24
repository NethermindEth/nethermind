// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class TxTypeExtensions
{
    // Frame transactions (EIP-8141) carry EIP-1559 fee fields but have no access list field.
    public static bool SupportsAccessList(this TxType txType)
        => txType >= TxType.AccessList && txType != TxType.DepositTx && txType != TxType.FrameTx;

    public static bool Supports1559(this TxType txType)
        => txType >= TxType.EIP1559 && txType != TxType.DepositTx;

    // EIP8141-GAP: frame transactions carry blob_versioned_hashes/max_fee_per_blob_gas fields, but
    // the spec does not define sidecar/network-wrapper handling; blob support is off in the prototype.
    public static bool SupportsBlobs(this TxType txType)
        => txType == TxType.Blob;

    public static bool SupportsAuthorizationList(this TxType txType)
        => txType == TxType.SetCode;

    public static bool SupportsFrames(this TxType txType)
        => txType == TxType.FrameTx;
}
