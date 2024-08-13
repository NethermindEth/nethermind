// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Nethermind.JsonRpc.Modules;

public interface IEraImporter
{
    event EventHandler<ImportProgressChangedArgs> ImportProgressChanged;

    event EventHandler<VerificationProgressArgs> VerificationProgress;

    Task Import(string src, long start, long end, string? accumulatorFile = null, CancellationToken cancellation = default);
    Task ImportAsArchiveSync(string src, CancellationToken cancellation);
    Task VerifyEraFiles(string[] eraFiles, byte[][] accumulators, CancellationToken cancellation = default);

    Task VerifyEraFiles(string eraDirectory, string accumulatorFile, CancellationToken cancellation = default);
}
