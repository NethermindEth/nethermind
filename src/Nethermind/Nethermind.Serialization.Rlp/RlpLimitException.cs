// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp;

public class RlpLimitException : RlpException
{
    public RlpLimitException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public RlpLimitException(string message)
        : base(message)
    {
    }
}
