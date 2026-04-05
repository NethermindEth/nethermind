// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EraE.Admin;
using Nethermind.JsonRpc;

namespace Nethermind.EraE.JsonRpc;

public class EraAdminRpcModule(IAdminEraService eraService) : IEraAdminRpcModule
{
    public Task<ResultWrapper<string>> admin_exportEraHistory(string destinationPath, long from, long to) =>
        Task.FromResult(eraService.ExportHistory(destinationPath, from, to));

    public Task<ResultWrapper<string>> admin_importEraHistory(string sourcePath, long from = 0, long to = 0, string? accumulatorFile = null) =>
        Task.FromResult(eraService.ImportHistory(sourcePath, from, to, accumulatorFile));
}
