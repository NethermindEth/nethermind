// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public interface IOverridableCodeInfoRepository : ICodeInfoRepository
{
    /// <summary>Serves <paramref name="value"/> as the code at <paramref name="key"/>.</summary>
    /// <remarks>
    /// The world state must already hold <paramref name="value"/>'s code at <paramref name="key"/>: the override is
    /// served only while the state keeps that code hash, and after a direct write (SETCODEFROM, EIP-7702) is reverted
    /// the original override is restored. A precompile <paramref name="value"/> carries no code and is exempt.
    /// </remarks>
    void SetCodeOverride(IReleaseSpec vmSpec, Address key, CodeInfo value);
    void MovePrecompile(IReleaseSpec vmSpec, Address precompileAddr, Address targetAddr);
    void ResetOverrides();
    void ResetPrecompileOverrides();
}
