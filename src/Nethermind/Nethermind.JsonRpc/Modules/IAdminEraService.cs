// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules;

public interface IAdminEraService
{
    ResultWrapper<string> ExportHistory(string destination, int epochFrom, int epochTo);
    ResultWrapper<string> VerifyHistory(string eraSource, string accumulatorFile);
}
