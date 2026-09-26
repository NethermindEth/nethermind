// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

public interface IAdminEraService
{
    string ExportHistory(string destination, ulong from, ulong to);
    string ImportHistory(string source, ulong from, ulong to, string? accumulatorFile);
}
