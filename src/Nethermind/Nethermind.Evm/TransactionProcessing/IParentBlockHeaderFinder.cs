// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.TransactionProcessing;

public interface IParentBlockHeaderFinder
{
    BlockHeader? FindParentHeader(BlockHeader block);
}
