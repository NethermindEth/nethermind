// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Taiko.Precompiles;

public class TaikoPrecompileProvider : IPrecompileProvider
{
    private static readonly FrozenDictionary<AddressAsKey, CodeInfo> _precompiles = CreatePrecompiles();

    private static FrozenDictionary<AddressAsKey, CodeInfo> CreatePrecompiles()
    {
        Dictionary<AddressAsKey, CodeInfo> dict = new(new EthereumPrecompileProvider().GetPrecompiles())
        {
            [L1SloadPrecompile.Address] = new(L1SloadPrecompile.Instance),
            [L1StaticCallPrecompile.Address] = new(L1StaticCallPrecompile.Instance),
        };
        return dict.ToFrozenDictionary();
    }

    public FrozenDictionary<AddressAsKey, CodeInfo> GetPrecompiles() => _precompiles;
}
