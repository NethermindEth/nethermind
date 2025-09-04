// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public interface IPrecompileFactory
{
    public IDictionary<AddressAsKey, PrecompileInfo> CreatePrecompiles();
}
