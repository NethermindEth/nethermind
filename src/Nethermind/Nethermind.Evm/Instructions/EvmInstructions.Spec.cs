// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static partial class EvmInstructions
{
    internal interface IAccessSpec
    {
        static abstract bool ConsumeAccountAccessGas<TGasPolicy>(ref TGasPolicy gas, IReleaseSpec spec,
            ref readonly StackAccessTracker tracker, bool tracing, Address address, AccountAccessKind kind = AccountAccessKind.Default)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy>;
    }

    internal readonly struct AccessSpec<Eip2929, Eip8038> : IAccessSpec
        where Eip2929 : struct, IFlag
        where Eip8038 : struct, IEip8038Flag
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ConsumeAccountAccessGas<TGasPolicy>(ref TGasPolicy gas, IReleaseSpec spec,
            ref readonly StackAccessTracker tracker, bool tracing, Address address, AccountAccessKind kind = AccountAccessKind.Default)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
            TGasPolicy.ConsumeAccountAccessGas<Eip2929, Eip8038>(ref gas, spec, in tracker, tracing, address, kind);
    }

    internal interface ICallSpec : IAccessSpec
    {
        static abstract bool UseHotAndColdStorage { get; }
        static abstract bool ClearEmptyAccountWhenTouched { get; }
        static abstract bool IsEip2780Enabled { get; }
        static abstract bool IsEip8038Enabled { get; }
        static abstract bool TryReserveChildGas<TGasPolicy>(ref TGasPolicy gas, in UInt256 requestedGas, IReleaseSpec spec, out ulong childGas)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy>;
    }

    internal readonly struct CallSpec<Eip2929, Eip150, Eip158, Eip2780, Eip8038> : ICallSpec
        where Eip2929 : struct, IFlag
        where Eip150 : struct, IFlag
        where Eip158 : struct, IFlag
        where Eip2780 : struct, IFlag
        where Eip8038 : struct, IEip8038Flag
    {
        public static bool UseHotAndColdStorage => Eip2929.IsActive;
        public static bool ClearEmptyAccountWhenTouched => Eip158.IsActive;
        public static bool IsEip2780Enabled => Eip2780.IsActive;
        public static bool IsEip8038Enabled => Eip8038.IsActive;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReserveChildGas<TGasPolicy>(ref TGasPolicy gas, in UInt256 requestedGas, IReleaseSpec spec, out ulong childGas)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
            TGasPolicy.TryReserveChildGas<Eip150>(ref gas, in requestedGas, spec, out childGas);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ConsumeAccountAccessGas<TGasPolicy>(ref TGasPolicy gas, IReleaseSpec spec,
            ref readonly StackAccessTracker tracker, bool tracing, Address address, AccountAccessKind kind = AccountAccessKind.Default)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
            TGasPolicy.ConsumeAccountAccessGas<Eip2929, Eip8038>(ref gas, spec, in tracker, tracing, address, kind);
    }

    internal interface ICreateSpec
    {
        static abstract bool UseHotAndColdStorage { get; }
        static abstract bool IsEip3860Enabled { get; }
        static abstract bool TryReserveChildGas<TGasPolicy>(ref TGasPolicy gas, IReleaseSpec spec, out ulong childGas)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy>;
        static abstract bool ConsumeCreateGas<TGasPolicy, Eip8037, TOpCreate>(ref TGasPolicy gas, IReleaseSpec spec, ulong words)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy>
            where Eip8037 : struct, IFlag
            where TOpCreate : struct, IOpCreate;
    }

    internal readonly struct CreateSpec<Eip2929, Eip150, Eip3860, Eip8038> : ICreateSpec
        where Eip2929 : struct, IFlag
        where Eip150 : struct, IFlag
        where Eip3860 : struct, IFlag
        where Eip8038 : struct, IEip8038Flag
    {
        public static bool UseHotAndColdStorage => Eip2929.IsActive;
        public static bool IsEip3860Enabled => Eip3860.IsActive;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReserveChildGas<TGasPolicy>(ref TGasPolicy gas, IReleaseSpec spec, out ulong childGas)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
            TGasPolicy.TryReserveChildGas<Eip150>(ref gas, spec, out childGas);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ConsumeCreateGas<TGasPolicy, Eip8037, TOpCreate>(ref TGasPolicy gas, IReleaseSpec spec, ulong words)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy>
            where Eip8037 : struct, IFlag
            where TOpCreate : struct, IOpCreate =>
            TGasPolicy.ConsumeCreateGas<Eip8037, TOpCreate, Eip3860, Eip8038>(ref gas, spec, words);
    }

    internal interface ISelfDestructSpec : IAccessSpec
    {
        static abstract bool UseShanghaiDDosProtection { get; }
        static abstract bool ClearEmptyAccountWhenTouched { get; }
        static abstract bool SelfdestructOnlyOnSameTransaction { get; }
        static abstract bool RemoveSelfdestructBurn { get; }
        static abstract bool IsEip8038Enabled { get; }
    }

    internal readonly struct SelfDestructSpec<TAccess, Eip150, Eip158, Eip6780, Eip8246, Eip8038> : ISelfDestructSpec
        where TAccess : struct, IAccessSpec
        where Eip150 : struct, IFlag
        where Eip158 : struct, IFlag
        where Eip6780 : struct, IFlag
        where Eip8246 : struct, IFlag
        where Eip8038 : struct, IEip8038Flag
    {
        public static bool UseShanghaiDDosProtection => Eip150.IsActive;
        public static bool ClearEmptyAccountWhenTouched => Eip158.IsActive;
        public static bool SelfdestructOnlyOnSameTransaction => Eip6780.IsActive;
        public static bool RemoveSelfdestructBurn => Eip8246.IsActive;
        public static bool IsEip8038Enabled => Eip8038.IsActive;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ConsumeAccountAccessGas<TGasPolicy>(ref TGasPolicy gas, IReleaseSpec spec,
            ref readonly StackAccessTracker tracker, bool tracing, Address address, AccountAccessKind kind = AccountAccessKind.Default)
            where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
            TAccess.ConsumeAccountAccessGas(ref gas, spec, in tracker, tracing, address, kind);
    }
}
