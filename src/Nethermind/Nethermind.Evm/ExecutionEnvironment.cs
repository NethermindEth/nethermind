// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Queue =
#if ZK_EVM
        Nethermind.Evm.ZkEvmQueue<Nethermind.Evm.ExecutionEnvironment>;
#else
        System.Collections.Concurrent.ConcurrentQueue<Nethermind.Evm.ExecutionEnvironment>;
#endif

namespace Nethermind.Evm
{
    /// <summary>
    /// Execution environment for EVM calls. Pooled to avoid allocation and GC write barrier overhead.
    /// </summary>
    public sealed class ExecutionEnvironment : IDisposable
    {
        private static readonly Queue _pool = new();
        private UInt256 _value;

        /// <summary>
        /// Parsed bytecode for the current call.
        /// </summary>
        public CodeInfo CodeInfo { get; private set; } = null!;

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
        /// Value information passed (it is different from transfer value in DELEGATECALL.
        /// DELEGATECALL behaves like a library call, and it uses the value information from the caller even
        /// as no transfer happens.
        /// </summary>
        public ref readonly UInt256 Value => ref _value;

        /// <summary>
        /// Parameters / arguments of the current call.
        /// </summary>
        public ReadOnlyMemory<byte> InputData { get; private set; }

        // The following describe how this frame was invoked. They are populated by
        // <see cref="VmState{TGasPolicy}"/> when the frame is rented and cleared on Dispose.

        /// <summary>
        /// Offset in the caller's memory where this frame's output is written.
        /// </summary>
        internal long OutputDestination { get; set; }

        /// <summary>
        /// Number of output bytes the caller expects written back into its memory.
        /// </summary>
        internal long OutputLength { get; set; }

        /// <summary>
        /// The kind of call that created this frame (CALL, DELEGATECALL, CREATE, ...).
        /// </summary>
        public ExecutionType ExecutionType { get; internal set; }

        /// <summary>
        /// Whether this is the top-level frame of the transaction.
        /// </summary>
        public bool IsTopLevel { get; internal set; }

        /// <summary>
        /// Whether this frame executes in a static context (no state modifications allowed).
        /// </summary>
        public bool IsStatic { get; internal set; }

        /// <summary>
        /// Whether this CREATE targets an account that already exists.
        /// </summary>
        public bool IsCreateOnPreExistingAccount { get; internal set; }

        /// <summary>
        /// Whether CREATE state gas has been charged for this frame.
        /// </summary>
        public bool IsCreateStateGasCharged { get; internal set; }

        /// <summary>
        /// EIP-8037: the parent <c>*CALL</c> charged NEW_ACCOUNT state gas up-front for this (dead)
        /// recipient; on this frame's error/revert no account is created, so the parent refunds it.
        /// </summary>
        public bool NewAccountCharged { get; internal set; }

        private ExecutionEnvironment() { }

        /// <summary>
        /// Rents an ExecutionEnvironment from the pool and initializes it with the provided values.
        /// </summary>
        public static ExecutionEnvironment Rent(
            CodeInfo codeInfo,
            Address executingAccount,
            Address caller,
            Address? codeSource,
            int callDepth,
            in UInt256 value,
            in ReadOnlyMemory<byte> inputData)
        {
            ExecutionEnvironment env = _pool.TryDequeue(out ExecutionEnvironment pooled) ? pooled : new();
            env.CodeInfo = codeInfo;
            env.ExecutingAccount = executingAccount;
            env.Caller = caller;
            env.CodeSource = codeSource;
            env.CallDepth = callDepth;
            env._value = value;
            env.InputData = inputData;
            return env;
        }

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
                _value = default;
                InputData = default;
                OutputDestination = 0;
                OutputLength = 0;
                ExecutionType = default;
                IsTopLevel = false;
                IsStatic = false;
                IsCreateOnPreExistingAccount = false;
                IsCreateStateGasCharged = false;
                NewAccountCharged = false;
                _pool.Enqueue(this);
            }
#if DEBUG
            GC.SuppressFinalize(this);
#endif
        }

#if DEBUG
        private readonly System.Diagnostics.StackTrace _creationStackTrace = new();

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
