// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class TxTypeExtensions
{
    // Frame transactions (EIP-8141) carry EIP-1559 fee fields but have no access list field.
    // Allowlists, so a plugin-registered transaction type gets none of these semantics by default.
    public static bool SupportsAccessList(this TxType txType)
        => txType is TxType.AccessList or TxType.EIP1559 or TxType.Blob or TxType.SetCode;

    public static bool Supports1559(this TxType txType)
        => txType is TxType.EIP1559 or TxType.Blob or TxType.SetCode or TxType.FrameTx;

    // Type-3 only; a blob-carrying frame tx is matched by Transaction.CarriesBlobs instead.
    public static bool SupportsBlobs(this TxType txType)
        => txType == TxType.Blob;

    public static bool SupportsAuthorizationList(this TxType txType)
        => txType == TxType.SetCode;

    public static bool SupportsFrames(this TxType txType)
        => txType == TxType.FrameTx;
}
