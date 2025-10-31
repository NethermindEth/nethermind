// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;

namespace Nethermind.EraE.JsonRpc;

public class EraAdminRpcModule(IAdminEraService eraService) : IEraAdminRpcModule
{
    public Task<ResultWrapper<string>> admin_exportHistory(string destination, int start = 0, int end = 0)
    {
        return ResultWrapper<string>.Success(eraService.ExportHistory(destination, start, end));
    }

    public Task<ResultWrapper<string>> admin_importHistory(string source, int start = 0, int end = 0, string? accumulatorFile = null, string? historicalRootsFile = null)
    {
        return ResultWrapper<string>.Success(eraService.ImportHistory(source, start, end, accumulatorFile, historicalRootsFile));
    }
}
