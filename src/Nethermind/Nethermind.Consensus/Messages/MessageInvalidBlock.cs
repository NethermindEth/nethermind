// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Messages;
public sealed class MessageInvalidBlock
{
    //    Exception raised when a block is invalid, but not due to a transaction.

    //E.g.all transactions in the block are valid, and can be applied to the state, but the
    //block header contains an invalid field.

    //Block's format is incorrect, contains invalid fields, is missing fields, or contains fields of
    //a fork that is not active yet.
    public string INCORRECT_BLOCK_FORMAT()
    {
        return "";
    }

    //Block's blob gas used in header is above the limit.
    public string BLOB_GAS_USED_ABOVE_LIMIT()
    {
        return "";
    }

    //Block's blob gas used in header is incorrect.
    public string INCORRECT_BLOB_GAS_USED()
    {
        return "";
    }

    //Block's excess blob gas in header is incorrect.
    public string INCORRECT_EXCESS_BLOB_GAS()
    {
        return "";
    }
}
