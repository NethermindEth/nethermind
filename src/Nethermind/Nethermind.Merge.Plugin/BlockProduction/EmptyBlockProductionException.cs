// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.BlockProduction;

public class EmptyBlockProductionException(string message) : System.Exception($"Couldn't produce empty block: {message}")
{
}
