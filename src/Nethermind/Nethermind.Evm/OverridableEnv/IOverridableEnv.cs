// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac.Core;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.OverridableEnv;

public interface IOverridableEnv: IModule
{
    IDisposable Build(Hash256 stateRoot);
    IDisposable BuildAndOverride(BlockHeader header, Dictionary<Address, AccountOverride>? stateOverride);
}

public interface IOverridableEnv<T>
{
    IDisposable Build(Hash256 stateRoot, out T component);
    IDisposable BuildAndOverride(BlockHeader header, out T component);
    IDisposable BuildAndOverride(BlockHeader header, Dictionary<Address, AccountOverride>? stateOverride, out T component);
}
