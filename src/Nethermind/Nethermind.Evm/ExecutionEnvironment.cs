// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public readonly struct ExecutionEnvironment
    {
        public ExecutionEnvironment
        (
            ICodeInfo codeInfo,
            Address executingAccount,
            Address caller,
            Address? codeSource,
            ReadOnlyMemory<byte> inputData,
            TxExecutionContext txExecutionContext,
            UInt256 transferValue,
            UInt256 value,
            int callDepth = 0)
        {
            CodeInfo = codeInfo;
            ExecutingAccount = executingAccount;
            Caller = caller;
            CodeSource = codeSource;
            InputData = inputData;
            TxExecutionContext = txExecutionContext;
            TransferValue = transferValue;
            Value = value;
            CallDepth = callDepth;
        }

        /// <summary>
        /// Currently executing account (in DELEGATECALL this will be equal to caller).
        /// </summary>
        public readonly Address ExecutingAccount;

        /// <summary>
        /// Caller
        /// </summary>
        public readonly Address Caller;

        /// <summary>
        /// Bytecode source (account address).
        /// </summary>
        public readonly Address? CodeSource;

        /// <summary>
        /// Parameters / arguments of the current call.
        /// </summary>
        public readonly ReadOnlyMemory<byte> InputData;

        /// <summary>
        /// Transaction originator
        /// </summary>
        public readonly TxExecutionContext TxExecutionContext;

        /// <summary>
        /// ETH value transferred in this call.
        /// </summary>
        public readonly UInt256 TransferValue;

        /// <summary>
        /// Value information passed (it is different from transfer value in DELEGATECALL.
        /// DELEGATECALL behaves like a library call and it uses the value information from the caller even
        /// as no transfer happens.
        /// </summary>
        public UInt256 Value { get; }

        /// <summary>
        /// Parsed bytecode for the current call.
        /// </summary>
        public ICodeInfo CodeInfo { get; }

        /// <example>If we call TX -> DELEGATECALL -> CALL -> STATICCALL then the call depth would be 3.</example>
        public readonly int CallDepth;
    }
}
