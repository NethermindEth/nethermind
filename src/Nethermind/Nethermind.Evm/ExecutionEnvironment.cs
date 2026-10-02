// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Queue = Nethermind.Evm.EvmObjectPool<Nethermind.Evm.ExecutionEnvironment>;

namespace Nethermind.Evm
{
    /// <summary>
    /// Execution environment for EVM calls. Pooled to avoid allocation and GC write barrier overhead.
    /// </summary>
    public sealed class ExecutionEnvironment : IDisposable
    {
        private static readonly Queue _pool = new();
        private UInt256 _value;

        /// <summary>Owned by a call-frame cache slot, so disposal clears it but never hands it to the pool.</summary>
        private bool _isCached;

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
            ExecutionEnvironment env = _pool.TryDequeue(out ExecutionEnvironment? pooled) ? pooled : new();
            env.Initialize(codeInfo, executingAccount, caller, codeSource, callDepth, in value, in inputData);
            return env;
        }

        /// <summary>
        /// Takes the environment for a child frame at <paramref name="callDepth"/> from <paramref name="cache"/>,
        /// indexed by call depth, falling back to the pool beyond its length.
        /// </summary>
        /// <remarks>
        /// A slot is reused only once its previous environment has been disposed. One still in use - left behind
        /// when an exception unwound past a frame that was staged but never entered - is abandoned to the GC and
        /// replaced, so a live environment is never shared.
        /// </remarks>
        internal static ExecutionEnvironment Rent(
            ExecutionEnvironment?[] cache,
            CodeInfo codeInfo,
            Address executingAccount,
            Address caller,
            Address? codeSource,
            int callDepth,
            in UInt256 value,
            in ReadOnlyMemory<byte> inputData)
        {
            Debug.Assert(executingAccount is not null, "A free cached environment is recognised by its cleared account.");
            ExecutionEnvironment env = (uint)callDepth < (uint)cache.Length && cache[callDepth] is { IsInUse: false } cached
                ? cached
                : RentUncached(cache, callDepth);
            env.Initialize(codeInfo, executingAccount, caller, codeSource, callDepth, in value, in inputData);
            return env;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ExecutionEnvironment RentUncached(ExecutionEnvironment?[] cache, int callDepth)
        {
            if ((uint)callDepth >= (uint)cache.Length)
            {
                return _pool.TryDequeue(out ExecutionEnvironment? pooled) ? pooled : new();
            }

            ExecutionEnvironment env = new() { _isCached = true };
            cache[callDepth] = env;
            return env;
        }

        /// <summary>Empties the slots of <paramref name="cache"/> whose environment is still in use, without disposing it.</summary>
        internal static void ForgetInUse(ExecutionEnvironment?[] cache)
        {
            for (int depth = 0; depth < cache.Length; depth++)
            {
                if (cache[depth] is { IsInUse: true })
                {
                    cache[depth] = null;
                }
            }
        }

        /// <summary>Rented and not yet disposed; <see cref="Dispose"/> clears the account.</summary>
        private bool IsInUse => ExecutingAccount is not null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Initialize(
            CodeInfo codeInfo,
            Address executingAccount,
            Address caller,
            Address? codeSource,
            int callDepth,
            in UInt256 value,
            in ReadOnlyMemory<byte> inputData)
        {
            PooledObjectLeakDetector.OnRent(this, nameof(ExecutionEnvironment));
            CodeInfo = codeInfo;
            ExecutingAccount = executingAccount;
            Caller = caller;
            CodeSource = codeSource;
            CallDepth = callDepth;
            _value = value;
            InputData = inputData;
        }

        /// <summary>
        /// Returns the ExecutionEnvironment to the pool for reuse.
        /// </summary>
        public void Dispose()
        {
            PooledObjectLeakDetector.OnReturn(this);
            if (IsInUse)
            {
                CodeInfo = null!;
                ExecutingAccount = null!;
                Caller = null!;
                CodeSource = null;
                CallDepth = 0;
                _value = default;
                InputData = default;
                if (!_isCached) _pool.Enqueue(this);
            }
        }
    }
}
