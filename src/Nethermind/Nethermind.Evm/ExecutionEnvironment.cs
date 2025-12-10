// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    /// <summary>
    /// Execution environment for EVM calls. Pooled to avoid allocation and GC write barrier overhead.
    /// </summary>
    public sealed class ExecutionEnvironment
    {
        private static readonly ConcurrentQueue<ExecutionEnvironment> _pool = new();

        /// <summary>
        /// Parsed bytecode for the current call.
        /// </summary>
        public ICodeInfo CodeInfo;

        /// <summary>
        /// Currently executing account (in DELEGATECALL this will be equal to caller).
        /// </summary>
        public Address ExecutingAccount;

        /// <summary>
        /// Caller
        /// </summary>
        public Address Caller;

        /// <summary>
        /// Bytecode source (account address).
        /// </summary>
        public Address? CodeSource;

        /// <example>If we call TX -> DELEGATECALL -> CALL -> STATICCALL then the call depth would be 3.</example>
        public int CallDepth;

        /// <summary>
        /// ETH value transferred in this call.
        /// </summary>
        public UInt256 TransferValue;

        /// <summary>
        /// Value information passed (it is different from transfer value in DELEGATECALL.
        /// DELEGATECALL behaves like a library call and it uses the value information from the caller even
        /// as no transfer happens.
        /// </summary>
        public UInt256 Value;

        /// <summary>
        /// Parameters / arguments of the current call.
        /// </summary>
        public ReadOnlyMemory<byte> InputData;

        private ExecutionEnvironment() { }

        /// <summary>
        /// Rents an ExecutionEnvironment from the pool and initializes it with the provided values.
        /// </summary>
        public static ExecutionEnvironment Rent(
            ICodeInfo codeInfo,
            Address executingAccount,
            Address caller,
            Address? codeSource,
            int callDepth,
            in UInt256 transferValue,
            in UInt256 value,
            in ReadOnlyMemory<byte> inputData)
        {
            ExecutionEnvironment env = _pool.TryDequeue(out ExecutionEnvironment pooled) ? pooled : new ExecutionEnvironment();
            env.CodeInfo = codeInfo;
            env.ExecutingAccount = executingAccount;
            env.Caller = caller;
            env.CodeSource = codeSource;
            env.CallDepth = callDepth;
            env.TransferValue = transferValue;
            env.Value = value;
            env.InputData = inputData;
            return env;
        }

        /// <summary>
        /// Returns the ExecutionEnvironment to the pool for reuse.
        /// </summary>
        public void Return()
        {
            CodeInfo = null;
            ExecutingAccount = null;
            Caller = null;
            CodeSource = null;
            CallDepth = 0;
            TransferValue = default;
            Value = default;
            InputData = default;
            _pool.Enqueue(this);
        }
    }
}
