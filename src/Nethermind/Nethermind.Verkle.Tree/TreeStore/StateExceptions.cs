// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Verkle.Tree.TreeStore;

public class VerkleStateException : Exception
{
    protected VerkleStateException()
    {
    }

    protected VerkleStateException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public class StateUnavailableExceptions : VerkleStateException
{
    public StateUnavailableExceptions()
    {
    }

    public StateUnavailableExceptions(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public class StateFlushException : VerkleStateException
{
    public StateFlushException()
    {
    }

    public StateFlushException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
