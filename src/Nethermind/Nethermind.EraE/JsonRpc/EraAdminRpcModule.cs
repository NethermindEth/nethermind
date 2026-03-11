// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;

namespace Nethermind.EraE.JsonRpc;

public class EraAdminRpcModule(IAdminEraService eraService) : IEraAdminRpcModule
{
    public Task<ResultWrapper<string>> admin_exportEraHistory(string destinationPath, int from, int to) =>
        ResultWrapper<string>.Success(eraService.ExportHistory(destinationPath, from, to));

    public Task<ResultWrapper<string>> admin_importEraHistory(string sourcePath, int from = 0, int to = 0, string? accumulatorFile = null) =>
        ResultWrapper<string>.Success(eraService.ImportHistory(sourcePath, from, to, accumulatorFile));
}
