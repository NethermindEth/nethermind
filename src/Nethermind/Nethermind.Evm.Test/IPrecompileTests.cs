// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Evm.Test;

public interface IPrecompileTests
{
    static virtual IEnumerable<string> TestFiles() => [];
}
