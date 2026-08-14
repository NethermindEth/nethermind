// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Test;

public interface IPrecompileTests
{
    static virtual IEnumerable<string> TestFiles() => [];

    static virtual IEnumerable<(string file, IReleaseSpec spec)> TestFilesWithSpec() => [];
}
