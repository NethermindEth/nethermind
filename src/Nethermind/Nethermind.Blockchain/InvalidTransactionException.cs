// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain;

public class InvalidTransactionException(BlockHeader header, string message, TransactionResult result, Exception? innerException = null) : InvalidBlockException(header, message, innerException)
{
    public TransactionResult Reason = result;
}
