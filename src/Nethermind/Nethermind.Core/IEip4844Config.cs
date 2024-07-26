// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// <see cref="Eip4844Constants" /> wrapper, made for testing convenience.
/// </summary>
public interface IEip4844Config
{
    ulong MaxBlobGasPerBlock { get; }
    ulong GasPerBlob { get; }
    int GetMaxBlobsPerBlock();
}

public class ConstantEip4844Config : IEip4844Config
{
    public ulong MaxBlobGasPerBlock => Eip4844Constants.MaxBlobGasPerBlock;
    public ulong GasPerBlob => Eip4844Constants.GasPerBlob;
    public int GetMaxBlobsPerBlock() => Eip4844Constants.GetMaxBlobsPerBlock();

    static ConstantEip4844Config() => Instance = new ConstantEip4844Config();
    private ConstantEip4844Config() { }

    public static IEip4844Config Instance { get; private set; }
}
