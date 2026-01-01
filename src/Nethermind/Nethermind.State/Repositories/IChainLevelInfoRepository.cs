// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State.Repositories
{
    public interface IChainLevelInfoRepository
    {
        void Delete(ulong number, BatchWrite? batch = null);
        void PersistLevel(ulong number, ChainLevelInfo level, BatchWrite? batch = null);
        BatchWrite StartBatch();
        ChainLevelInfo? LoadLevel(ulong number);
        IOwnedReadOnlyList<ChainLevelInfo?> MultiLoadLevel(in ArrayPoolListRef<ulong> blockNumbers);
    }
}
