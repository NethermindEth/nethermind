// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State.Repositories
{
    public interface IChainLevelInfoRepository
    {
        void Delete(long number, BatchWrite? batch = null);
        void PersistLevel(long number, ChainLevelInfo level, BatchWrite? batch = null);
        BatchWrite StartBatch();
        ChainLevelInfo? LoadLevel(long number);
        IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(IReadOnlyList<long> blockNumbers);
    }
}
