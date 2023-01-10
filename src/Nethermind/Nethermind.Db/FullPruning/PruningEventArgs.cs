// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.FullPruning;

public class PruningEventArgs : EventArgs
{
    public PruningEventArgs(IPruningContext context, bool success)
    {
        Context = context;
        Success = success;
    }

    public IPruningContext Context { get; }

    public bool Success { get; }
}
