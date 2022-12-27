// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Processing;

public interface IReadOnlyTxProcessorSourceExt: IReadOnlyTxProcessorSource
{
    public IBlockTree BlockTree { get; }
}
