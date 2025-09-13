// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public interface IPrecompileProvider
{
    public FrozenDictionary<AddressAsKey, PrecompileInfo> GetPrecompiles();
}
