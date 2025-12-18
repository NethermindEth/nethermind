// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain;

public interface IReadOnlyTxProcessorSource
{
    IReadOnlyTxProcessingScope Build(BlockHeader? baseBlock);
}
