// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;

namespace Ethereum.Test.Base;

internal static class StateDumpHelper
{
    public static void Write(
        Action<string, string, long, Hash256, string>? stateDumper,
        string testName,
        string phase,
        long blockNumber,
        IWorldState stateProvider,
        IStateReader stateReader)
    {
        if (stateDumper is null)
        {
            return;
        }

        Hash256 stateRoot = stateProvider.StateRoot;
        BlockHeader stateHeader = CreateStateHeader(blockNumber, stateRoot);
        stateDumper(testName, phase, blockNumber, stateRoot, stateReader.DumpState(stateHeader));
    }

    public static void TryWrite(
        Action<string, string, long, Hash256, string>? stateDumper,
        string testName,
        string phase,
        long blockNumber,
        IWorldState stateProvider,
        IStateReader stateReader)
    {
        try
        {
            Write(stateDumper, testName, phase, blockNumber, stateProvider, stateReader);
        }
        catch
        {
            // Best-effort diagnostics should not hide the original test failure.
        }
    }

    private static BlockHeader CreateStateHeader(long blockNumber, Hash256 stateRoot) =>
        new(
            Keccak.Zero,
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            UInt256.Zero,
            blockNumber,
            0,
            0,
            [])
        {
            StateRoot = stateRoot
        };
}
