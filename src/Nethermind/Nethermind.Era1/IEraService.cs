// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

public interface IEraService
{
    Task Export(string destinationPath, string network, long start, long count, CancellationToken cancellation = default);
    bool VerifyEraFiles(string path);
}