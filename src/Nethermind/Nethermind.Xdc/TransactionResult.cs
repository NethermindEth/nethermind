// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;
using System;
using System.Collections.Generic;
using System.Text;
using static Nethermind.Evm.TransactionProcessing.TransactionResult;

namespace Nethermind.Xdc;

internal struct XdcTransactionResult
{
    public const ErrorType ContainsBlacklistedAddressError = (ErrorType)12;
    public const ErrorType NonceTooHighError = (ErrorType)13;
    public const ErrorType NonceTooLowError = (ErrorType)14;

    public static TransactionResult ContainsBlacklistedAddress => (TransactionResult)ContainsBlacklistedAddressError;
    public static TransactionResult NonceTooHigh => (TransactionResult)NonceTooHighError;
    public static TransactionResult NonceTooLow => (TransactionResult)NonceTooLowError;
}
