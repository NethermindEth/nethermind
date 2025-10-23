// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE;

public interface IAdminEraService: Era1.IAdminEraService
{
    string ImportHistory(string source, long from, long to, string? accumulatorFile, string? historicalRootsFile);
}
