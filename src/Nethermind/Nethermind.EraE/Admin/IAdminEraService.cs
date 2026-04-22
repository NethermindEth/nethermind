// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;

namespace Nethermind.EraE.Admin;

public interface IAdminEraService
{
    ResultWrapper<string> ExportHistory(string destination, long from, long to);
    ResultWrapper<string> ImportHistory(string source, long from, long to, string? accumulatorFile);
}
