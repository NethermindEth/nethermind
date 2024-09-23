// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IGetPayloadBodiesByRangeV2Handler
{
    Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>> Handle(long start, long count);
}
