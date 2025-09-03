// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain;

public interface IGenesisPostProcessor
{
    void PostProcess(Block genesis);
}

public sealed class NullGenesisPostProcessor : IGenesisPostProcessor
{
    public void PostProcess(Block genesis) { }
}
