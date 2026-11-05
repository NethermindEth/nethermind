// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.FullPruning;

public class PruningEventArgs(IPruningContext context, bool success) : EventArgs
{
    public IPruningContext Context { get; } = context;

    public bool Success { get; } = success;
}
