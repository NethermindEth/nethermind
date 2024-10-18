// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.TransactionProcessing;

public interface IOverridableTxProcessorSource
{
    IOverridableTxProcessingScope Build(Hash256 stateRoot);
    IOverridableTxProcessingScope BuildAndOverride(BlockHeader header, Dictionary<Address, AccountOverride> stateOverride);
}
