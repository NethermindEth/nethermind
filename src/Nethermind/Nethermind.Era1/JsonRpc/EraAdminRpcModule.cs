// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;

namespace Nethermind.Era1.JsonRpc;

public class EraAdminRpcModule(IAdminEraService eraService) : IEraAdminRpcModule
{
    public Task<ResultWrapper<string>> admin_exportHistory(string destination, int start = 0, int end = 0) =>
        start < 0 || end < 0
            ? ResultWrapper<string>.Fail("start and end must be non-negative", ErrorCodes.InvalidParams)
            : ResultWrapper<string>.Success(eraService.ExportHistory(destination, (ulong)start, (ulong)end));

    public Task<ResultWrapper<string>> admin_importHistory(string source, int start = 0, int end = 0, string? accumulatorFile = null) =>
        start < 0 || end < 0
            ? ResultWrapper<string>.Fail("start and end must be non-negative", ErrorCodes.InvalidParams)
            : ResultWrapper<string>.Success(eraService.ImportHistory(source, (ulong)start, (ulong)end, accumulatorFile));
}
