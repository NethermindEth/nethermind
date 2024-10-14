// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.Evm.TransactionProcessing;

public interface IOverridableTxProcessorSource
{
    IOverridableTxProcessingScope Build(Hash256 stateRoot);
}
