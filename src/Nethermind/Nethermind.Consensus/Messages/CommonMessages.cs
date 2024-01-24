// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Messages;
public static class CommonMessages
{
    //    Exception raised when a block is invalid, but not due to a transaction.

    //E.g.all transactions in the block are valid, and can be applied to the state, but the
    //block header contains an invalid field.

    //Block's format is incorrect, contains invalid fields, is missing fields, or contains fields of
    //a fork that is not active yet.
    public static string INCORRECT_BLOCK_FORMAT()
    {
        return "";
    }

    public static string HeaderGasUsedMismatch()
    {
        return $"HeaderGasUsedMismatch: Gas used in header does not match calculated.";
    }

    //Block's blob gas used in header is above the limit.
    public static string BlobGasUsedAboveBlockLimit()
        => $"BlockBlobGasExceeded: A block cannot have more than {Eip4844Constants.MaxBlobGasPerBlock} blob gas.";


    //Block's blob gas used in header is incorrect.
    public static string INCORRECT_BLOB_GAS_USED()
    {
        return "";
    }

    //Block's excess blob gas in header is incorrect.
    public static string IncorrectExcessBlobGas()
        => $"HeaderExcessBlobGasMismatch: Excess blob gas in header does not match calculated.";

    public static string HeaderBlobGasMismatch()
    {
        return $"HeaderBlobGasMismatch: Blob gas in header does not match calculated.";
    }

    public static string InvalidBlobData()
        => $"InvalidTxBlobData: Number of blobs, hashes, commitments and proofs must match.";

    public static string ExpectationFailed(string name, string expected, string actual)
    {
        return $"Invalid {name}: Expected {expected}, got {actual}.";
    }

}
