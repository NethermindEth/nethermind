// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE.Admin;

public interface IAdminEraService
{
    string ExportHistory(string destination, long from, long to);
    string ImportHistory(string source, long from, long to, string? accumulatorFile);
}
