// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data.V1;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IGetPayloadBodiesByRangeV1Handler
{
    Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> Handle(long start, long count);
}
