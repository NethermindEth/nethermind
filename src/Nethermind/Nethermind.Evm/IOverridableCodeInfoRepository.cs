// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public interface IOverridableCodeInfoRepository : ICodeInfoRepository
{
    /// <summary>Whether account overrides replace native precompiles with account code.</summary>
    bool DisablePrecompileOnAccountOverride { get; }
    /// <summary>Makes <paramref name="key"/> run <paramref name="value"/> until the account's code changes in the world state.</summary>
    /// <remarks>
    /// Write the code to the world state first. The override records the account's code hash when it is set, so code
    /// written afterwards ends it at once.
    /// A precompile remains native when <see cref="DisablePrecompileOnAccountOverride"/> is false.
    /// </remarks>
    void SetCodeOverride(IReleaseSpec vmSpec, Address key, CodeInfo value);
    void MovePrecompile(IReleaseSpec vmSpec, Address precompileAddr, Address targetAddr);
    void ResetOverrides();
    void ResetPrecompileOverrides(IReleaseSpec spec);
}
