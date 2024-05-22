// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing;

public interface IBlockCachePreWarmer
{
    Task PreWarmCaches(Block suggestedBlock, Hash256 parentStateRoot, CancellationToken cancellationToken = default);
}
