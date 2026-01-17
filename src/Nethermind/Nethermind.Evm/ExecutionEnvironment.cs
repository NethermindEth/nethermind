// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    /// <summary>
    /// Execution environment for EVM calls. Pooled to avoid allocation and GC write barrier overhead.
    /// </summary>
    public sealed class ExecutionEnvironment : IDisposable
    {
        private static readonly ConcurrentQueue<ExecutionEnvironment> _pool = new();
        private UInt256 _value;
        private UInt256 _transferValue;

        /// <summary>
        /// Parsed bytecode for the current call.
        /// </summary>
        public ICodeInfo CodeInfo { get; private set; } = null!;

        /// <summary>
        /// Currently executing account (in DELEGATECALL this will be equal to caller).
        /// </summary>
        public Address ExecutingAccount { get; private set; } = null!;

        /// <summary>
        /// Caller
        /// </summary>
        public Address Caller { get; private set; } = null!;

        /// <summary>
        /// Bytecode source (account address).
        /// </summary>
        public Address? CodeSource { get; private set; }

        /// <example>If we call TX -> DELEGATECALL -> CALL -> STATICCALL then the call depth would be 3.</example>
        public int CallDepth { get; private set; }

        /// <summary>
        /// ETH value transferred in this call.
        /// </summary>
        public ref readonly UInt256 TransferValue => ref _transferValue;

        /// <summary>
        /// Value information passed (it is different from transfer value in DELEGATECALL.
        /// DELEGATECALL behaves like a library call, and it uses the value information from the caller even
        /// as no transfer happens.
        /// </summary>
        public ref readonly UInt256 Value => ref _value;

        /// <summary>
        /// Parameters / arguments of the current call.
        /// </summary>
        public ReadOnlyMemory<byte> InputData { get; private set; }

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
            ExecutionEnvironment env = Rent();
            env.CodeInfo = codeInfo;
            env.ExecutingAccount = executingAccount;
            env.Caller = caller;
            env.CodeSource = codeSource;
            env.CallDepth = callDepth;
            env._transferValue = transferValue;
            env._value = value;
            env.InputData = inputData;
            return env;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ExecutionEnvironment Rent()
            => _pool.TryDequeue(out ExecutionEnvironment pooled) ? pooled : new ExecutionEnvironment();

        /// <summary>
        /// Returns the ExecutionEnvironment to the pool for reuse.
        /// </summary>
        public void Dispose()
        {
            if (ExecutingAccount is not null)
            {
                CodeInfo = null!;
                ExecutingAccount = null!;
                Caller = null!;
                CodeSource = null;
                CallDepth = 0;
                _transferValue = default;
                _value = default;
                InputData = default;
                _pool.Enqueue(this);
            }
#if DEBUG
            GC.SuppressFinalize(this);
#endif
        }

#if DEBUG

        private readonly StackTrace _creationStackTrace = new();

        ~ExecutionEnvironment()
        {
            if (ExecutingAccount is null)
            {
                Console.Error.WriteLine($"Warning: {nameof(ExecutionEnvironment)} was not disposed. Created at: {_creationStackTrace}");
            }
        }
#endif
    }
}
