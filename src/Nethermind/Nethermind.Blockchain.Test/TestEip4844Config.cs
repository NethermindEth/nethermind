// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Producers.Test;

public class TestEip4844Config : IEip4844Config
{
    public TestEip4844Config(ulong? maxBlobGasPerBlock = null)
    {
        MaxBlobGasPerBlock = maxBlobGasPerBlock ?? Eip4844Constants.MaxBlobGasPerBlock;
    }

    public ulong MaxBlobGasPerBlock { get; }

    public ulong GasPerBlob => Eip4844Constants.GasPerBlob;

    public int GetMaxBlobsPerBlock() => (int)(MaxBlobGasPerBlock / GasPerBlob);
}
