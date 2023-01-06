// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.EngineApi.Paris.Data;

namespace Nethermind.Merge.Plugin.EngineApi.Paris.Handlers;

public interface IGetPayloadBodiesByRangeV1Handler
{
    Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> Handle(long start, long count);
}
