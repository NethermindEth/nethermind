// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.Admin;

public interface IAdminEraService
{
    ResultWrapper<string> ExportHistory(string destination, long from, long to);
    ResultWrapper<string> ImportHistory(string source, string? accumulatorFile, long from, long to);
}
