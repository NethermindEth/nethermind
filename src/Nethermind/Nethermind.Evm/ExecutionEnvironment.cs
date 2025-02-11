// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
            CodeInfo codeInfo,
            Address executingAccount,
            Address caller,
            Address? codeSource,
            ReadOnlyMemory<byte> inputData,
            in TxExecutionContext txExecutionContext,
            UInt256 transferValue,
            UInt256 value,
            int callDepth = 0,
            long outputDestination = 0,
            long outputLength = 0,
            ExecutionType executionType = ExecutionType.TRANSACTION,
            bool isTopLevel = true,
            bool isStatic = false,
            bool isContinuation = false,
            bool isCreateOnPreExistingAccount = false)
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
            OutputDestination = outputDestination;
            OutputLength = outputLength;
            ExecutionType = executionType;
            IsTopLevel = isTopLevel;
            IsStatic = isStatic;
            IsContinuation = isContinuation;
            IsCreateOnPreExistingAccount = isCreateOnPreExistingAccount;
        }

        /// <summary>
        /// Parsed bytecode for the current call.
        /// </summary>
        public readonly CodeInfo CodeInfo;

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
        public readonly UInt256 Value;

        /// <example>If we call TX -> DELEGATECALL -> CALL -> STATICCALL then the call depth would be 3.</example>
        public readonly int CallDepth;

        /// <summary>
        /// Destination in memory where the output should be written.
        /// </summary>
        public readonly long OutputDestination;

        /// <summary>
        /// Length of the output to be written.
        /// </summary>
        public readonly long OutputLength;

        /// <summary>
        /// Type of execution (CALL, STATICCALL, etc.).
        /// </summary>
        public readonly ExecutionType ExecutionType;

        /// <summary>
        /// Whether this is a top-level call.
        /// </summary>
        public readonly bool IsTopLevel;

        /// <summary>
        /// Whether this is a static call.
        /// </summary>
        public readonly bool IsStatic;

        /// <summary>
        /// Whether this is a continuation call.
        /// </summary>
        public readonly bool IsContinuation;

        /// <summary>
        /// Whether this is a create operation on a pre-existing account.
        /// </summary>
        public readonly bool IsCreateOnPreExistingAccount;
    }
}
