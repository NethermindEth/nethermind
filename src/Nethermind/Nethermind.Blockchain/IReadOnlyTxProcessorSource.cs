// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain;

public interface IReadOnlyTxProcessorSource
{
    IReadOnlyTxProcessingScope Build(Hash256 stateRoot);
}
