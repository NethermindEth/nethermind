// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public readonly struct ExecutionEnvironment(
        ICodeInfo codeInfo,
        Address executingAccount,
        Address caller,
        Address? codeSource,
        ReadOnlyMemory<byte> inputData,
        in TxExecutionContext txExecutionContext,
        UInt256 transferValue,
        UInt256 value,
        int callDepth = 0)
    {
        /// <summary>
        /// Parsed bytecode for the current call.
        /// </summary>
        public readonly ICodeInfo CodeInfo = codeInfo;

        /// <summary>
        /// Currently executing account (in DELEGATECALL this will be equal to caller).
        /// </summary>
        public readonly Address ExecutingAccount = executingAccount;

        /// <summary>
        /// Caller
        /// </summary>
        public readonly Address Caller = caller;

        /// <summary>
        /// Bytecode source (account address).
        /// </summary>
        public readonly Address? CodeSource = codeSource;

        /// <summary>
        /// Parameters / arguments of the current call.
        /// </summary>
        public readonly ReadOnlyMemory<byte> InputData = inputData;

        /// <example>If we call TX -> DELEGATECALL -> CALL -> STATICCALL then the call depth would be 3.</example>
        public readonly int CallDepth = callDepth;

        /// <summary>
        /// ETH value transferred in this call.
        /// </summary>
        public readonly UInt256 TransferValue = transferValue;

        /// <summary>
        /// Value information passed (it is different from transfer value in DELEGATECALL.
        /// DELEGATECALL behaves like a library call and it uses the value information from the caller even
        /// as no transfer happens.
        /// </summary>
        public readonly UInt256 Value = value;

        /// <summary>
        /// Transaction context
        /// </summary>
        public readonly TxExecutionContext TxExecutionContext = txExecutionContext;
    }
}
