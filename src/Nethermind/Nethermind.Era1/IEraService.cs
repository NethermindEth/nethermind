// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Era1;

public interface IEraService
{
    Task Export(string destinationPath, string network, long start, long end, int size = EraWriter.MaxEra1Size, CancellationToken cancellation = default);
    Task Import(string src, string network, CancellationToken cancellation);
    Task<bool> VerifyEraFiles(string path);
}
