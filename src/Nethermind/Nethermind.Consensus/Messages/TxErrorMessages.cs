// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Crypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Messages;
public static class TxErrorMessages
{
    public static string InvalidTxType(string name) =>
        $"InvalidTxType: Transaction type in {name} is not supported.";
    public const string IntrinsicGasTooLow =
        "IntrinsicGasTooLow: Gas limit is too low.";

    public const string InvalidTxSignature =
        "InvalidTxSignature: Signature is invalid.";
    public static string InvalidTxChainId(ulong expected, ulong? actual) =>
        $"InvalidTxChainId: Expected {expected}, got {actual}.";

    public const string InvalidMaxPriorityFeePerGas =
        "InvalidMaxPriorityFeePerGas: Cannot be higher than maxFeePerGas.";

    public const string ContractSizeTooBig =
        "ContractSizeTooBig: Max initcode size exceeded.";

    public const string NotAllowedMaxFeePerBlobGas =
        "NotAllowedMaxFeePerBlobGas: Cannot be set.";

    public const string NotAllowedBlobVersionedHashes =
        "NotAllowedBlobVersionedHashes: Cannot be set.";

    public const string InvalidTransaction =
        $"InvalidTransaction: Cannot be {nameof(ShardBlobNetworkWrapper)}.";

    public const string TxMissingTo =
        "TxMissingTo: Must be set.";

    public const string BlobTxMissingMaxFeePerBlobGas =
        "BlobTxMissingMaxFeePerBlobGas: Must be set.";

    public const string BlobTxMissingBlobVersionedHashes =
        "BlobTxMissingBlobVersionedHashes: Must be set.";

    public static string BlobTxGasLimitExceeded =>
        $"BlobTxGasLimitExceeded: Transaction exceeded {Eip4844Constants.MaxBlobGasPerTransaction}.";

    public const string BlobTxMissingBlobs =
        "BlobTxMissingBlobs: Blob transaction must have blobs.";

    public const string MissingBlobVersionedHash =
        "MissingBlobVersionedHash: Must be set.";

    public static string InvalidBlobVersionedHashSize =>
        $"InvalidBlobVersionedHashSize: Cannot exceed {KzgPolynomialCommitments.BytesPerBlobVersionedHash}.";

    public const string InvalidBlobVersionedHashVersion =
        "InvalidBlobVersionedHashVersion: Blob version not supported.";

    public static string ExceededBlobSize =>
        $"ExceededBlobSize: Cannot be more than {Ckzg.Ckzg.BytesPerBlob}.";

    public static string ExceededBlobCommitmentSize =>
        $"ExceededBlobCommitmentSize: Cannot be more than {Ckzg.Ckzg.BytesPerCommitment}.";

    public static string InvalidBlobProofSize =>
        $"InvalidBlobProofSize: Cannot be more than {Ckzg.Ckzg.BytesPerProof}.";

    public const string InvalidBlobCommitmentHash =
        "InvalidBlobCommitmentHash: Commitment hash does not match.";

    public const string InvalidBlobProof =
        "InvalidBlobProof: Proof does not match.";
    public static string InvalidBlobData()
        => $"InvalidTxBlobData: Number of blobs, hashes, commitments and proofs must match.";
}
