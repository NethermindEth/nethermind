// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac.Core;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// An <see cref="IOverridableEnv"/> is an environment where the world state and or the code repository can be overridden.
/// It is an <see cref="IModule"/> that can be used to configure an autofac child lifetime to provide the necessary components
/// that reflect the override. Any components within that lifetime should run between the build and dispose of the returned
/// disposable or there may be memory leak.
/// </summary>
public interface IOverridableEnv : IModule
{
    IDisposable BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride);
}

/// <summary>
/// I small wrapper around <see cref="IOverridableEnv"/> to help prevent accidentally using components that rely on
/// the overridden env outside of the env scope. Ideally, no other components from the child lifetime is extracted
/// aside from <see cref="IOverridableEnv{T}"/>. To use any components such as <see cref="ITransactionProcessor"/>, set the
/// <see cref="T"/> here to that component, then call one of the method here to get it.
/// Always dispose the scope when finished.
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IOverridableEnv<T>
{
    Scope<T> BuildAndOverride(BlockHeader? header);
    Scope<T> BuildAndOverride(BlockHeader? header, Dictionary<Address, AccountOverride>? stateOverride);
}
