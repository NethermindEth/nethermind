// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public static class RpcTransactionErrors
{
    public const string ContractCreationWithoutData = "contract creation without any data provided";
    public const string GasPriceInEip1559 = "both gasPrice and (maxFeePerGas or maxPriorityFeePerGas) specified";
    public const string AtLeastOneBlobInBlobTransaction = "need at least 1 blob for a blob transaction";
    public const string InvalidBlobVersionedHashSize = "blob versioned hash must be 32 bytes";
    public const string InvalidBlobVersionedHashVersion = "blob versioned hash version must be 0x01";
    public const string MissingToInBlobTx = "missing \"to\" in blob transaction";
    public const string ZeroMaxFeePerBlobGas = "maxFeePerBlobGas, if specified, must be non-zero";

    public static string MaxFeePerGasSmallerThanMaxPriorityFeePerGas(UInt256? maxFeePerGas, UInt256? maxPriorityFeePerGas)
        => $"maxFeePerGas ({maxFeePerGas}) < maxPriorityFeePerGas ({maxPriorityFeePerGas})";

    public static string NullEntryIn(string field) => $"{field} must not contain a null entry";

    /// <summary>Reports the gas an EIP-8141 frame transaction reserves against the RPC cap: its frame gas
    /// limits plus the signature verification the processor runs before deriving any budget.</summary>
    public static string FrameGasAboveCap(ulong reservedGas, ulong gasCap)
        => $"frame gas limits and signature verification ({reservedGas}) exceed the gas cap ({gasCap})";
}
