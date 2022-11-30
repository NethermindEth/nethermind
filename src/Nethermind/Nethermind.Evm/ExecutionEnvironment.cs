// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public struct ExecutionEnvironment
    {
        /// <summary>
        /// Transaction originator
        /// </summary>
        public TxExecutionContext TxExecutionContext { get; set; }

        /// <summary>
        /// Currently executing account (in DELEGATECALL this will be equal to caller).
        /// </summary>
        public Address ExecutingAccount { get; set; }

        /// <summary>
        /// Caller
        /// </summary>
        public Address Caller { get; set; }

        /// <summary>
        /// Bytecode source (account address).
        /// </summary>
        public Address? CodeSource { get; set; }

        /// <summary>
        /// Parameters / arguments of the current call.
        /// </summary>
        public ReadOnlyMemory<byte> InputData { get; set; }

        /// <summary>
        /// ETH value transferred in this call.
        /// </summary>
        public UInt256 TransferValue { get; set; }

        /// <summary>
        /// Value information passed (it is different from transfer value in DELEGATECALL.
        /// DELEGATECALL behaves like a library call and it uses the value information from the caller even
        /// as no transfer happens.
        /// </summary>
        public UInt256 Value { get; set; }

        /// <summary>
        /// Parsed bytecode for the current call.
        /// </summary>
        public CodeInfo CodeInfo { get; set; }

        /// <example>If we call TX -> DELEGATECALL -> CALL -> STATICCALL then the call depth would be 3.</example>
        public int CallDepth { get; set; }
    }
}
