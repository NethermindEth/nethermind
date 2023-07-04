// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IGetPayloadBodiesByRangeV1Handler
{
    Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>> Handle(long start, long count);
}
