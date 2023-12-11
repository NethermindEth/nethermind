// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Era1;

namespace Nethermind.Blockchain;

public interface IEraExporter
{
    Task Export(string destinationPath, long start, long end, int size = EraWriter.MaxEra1Size, CancellationToken cancellation = default);
    Task<bool> VerifyEraFiles(string[] eraFiles, byte[][] accumulators, CancellationToken cancellation = default);
}
