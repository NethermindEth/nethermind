// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.Test;

public interface IPrecompileTests
{
    static abstract IEnumerable<string> TestFiles();
    static abstract IPrecompile Precompile();
}
