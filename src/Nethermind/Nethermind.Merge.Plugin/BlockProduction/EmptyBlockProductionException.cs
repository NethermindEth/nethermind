// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.BlockProduction;

public class EmptyBlockProductionException : System.Exception
{
    public EmptyBlockProductionException(string message)
        : base($"Couldn't produce empty block: {message}") { }
}
