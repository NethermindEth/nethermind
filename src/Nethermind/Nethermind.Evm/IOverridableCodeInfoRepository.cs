// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

public interface IOverridableCodeInfoRepository : ICodeInfoRepository
{
    void SetCodeOverwrite(IReleaseSpec vmSpec, Address key, ICodeInfo value);
    void MovePrecompile(IReleaseSpec vmSpec, Address precompileAddr, Address targetAddr);
    void ResetOverrides();
    void ResetPrecompileOverrides();
}
