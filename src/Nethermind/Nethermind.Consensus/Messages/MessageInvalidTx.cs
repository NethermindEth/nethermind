// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Messages;
public sealed class MessageInvalidTx
{
    //Exception raised when a transaction is invalid, and thus cannot be executed.
    //If a transaction with any of these exceptions is included in a block, the block is invalid.

    //Transaction's sender does not have enough funds to pay for the transaction.
    public string INSUFFICIENT_ACCOUNT_FUNDS()
    {
        return "";
    }

    //Transaction's max-fee-per-gas is lower than the block base-fee.
    public string INSUFFICIENT_MAX_FEE_PER_GAS()
    {
        return "";
    }

    //Transaction's max-priority-fee-per-gas is greater than the max-fee-per-gas.
    public string PRIORITY_GREATER_THAN_MAX_FEE_PER_GAS()
    {
        return "";
    }

    //Transaction's max-fee-per-blob-gas is lower than the block's blob-gas price.
    public string INSUFFICIENT_MAX_FEE_PER_BLOB_GAS()
    {
        return "";
    }

    //Transaction's gas limit is too low.    
    public string INTRINSIC_GAS_TOO_LOW()
    {
        return "";
    }

    //Transaction's initcode for a contract-creating transaction is too large.
    public string INITCODE_SIZE_EXCEEDED()
    {
        return "";
    }
    //Transaction type 3 included before activation fork.
    public string TYPE_3_TX_PRE_FORK()
    {
        return "";
    }
    //Transaction type 3, with zero blobs, included before activation fork.
    public string TYPE_3_TX_ZERO_BLOBS_PRE_FORK()
    {
        return "";
    }
    //Transaction contains a blob versioned hash with an invalid version.
    public string TYPE_3_TX_INVALID_BLOB_VERSIONED_HASH()
    {
        return "";
    }
    //Transaction contains full blobs (network-version of the transaction).
    public string TYPE_3_TX_WITH_FULL_BLOBS()
    {
        return "";
    }
    //Transaction contains too many blob versioned hashes.
    public string TYPE_3_TX_BLOB_COUNT_EXCEEDED()
    {
        return "";
    }
    //Transaction is a type 3 transaction and has an empty `to`.
    public string TYPE_3_TX_CONTRACT_CREATION()
    {
        return "";
    }

    //Transaction causes block to go over blob gas limit.
    public string TYPE_3_TX_MAX_BLOB_GAS_ALLOWANCE_EXCEEDED()
    {
        return "";
    }
    //Transaction is type 3, but has no blobs.
    public string TYPE_3_TX_ZERO_BLOBS()
    {
        return "";
    }
}
