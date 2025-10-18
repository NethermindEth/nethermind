// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.EraE;
public class EraStore(
    ISpecProvider specProvider,
    IBlockValidator blockValidator,
    IFileSystem fileSystem,
    string networkName,
    int maxEraSize,
    ISet<ValueHash256>? trustedAcccumulators,
    string directory,
    int verifyConcurrency = 0,
    string checksumsFileName = EraExporter.ChecksumsFileName
    ) : Era1.EraStore(
        specProvider, 
        blockValidator, 
        fileSystem, 
        networkName, 
        maxEraSize, 
        trustedAcccumulators, 
        directory,
        verifyConcurrency,
        checksumsFileName
    );
