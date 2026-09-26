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

    /// <summary>Reports the gas an EIP-8141 frame transaction reserves against the RPC cap.</summary>
    /// <remarks>The two terms are reported apart so the caller can see which one it has to shrink; either
    /// may be zero, and a signature-free transaction is the common case.</remarks>
    /// <param name="frameGas">The sum of the frame gas limits.</param>
    /// <param name="signatureGas">The signature verification the processor runs before deriving any budget.</param>
    /// <param name="gasCap">The cap the reservation exceeded.</param>
    public static string FrameGasAboveCap(ulong frameGas, ulong signatureGas, ulong gasCap)
        => $"frame gas limits ({frameGas}) and signature verification ({signatureGas}) exceed the gas cap ({gasCap})";
}
