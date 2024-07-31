// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Nethermind.Synchronization;

public interface IEraImporter
{
    event EventHandler<VerificationProgressArgs> VerificationProgress;

    event EventHandler<ImportProgressChangedArgs> ImportProgressChanged;

    Task VerifyEraFiles(string[] eraFiles, byte[][] accumulators, CancellationToken cancellation = default);

    Task VerifyEraFiles(string eraDirectory, string accumulatorFile, CancellationToken cancellation = default);
}
