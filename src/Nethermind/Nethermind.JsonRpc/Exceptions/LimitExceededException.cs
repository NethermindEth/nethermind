// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Exceptions;

public class LimitExceededException : Exception
{
    public LimitExceededException(string message)
        : base(message)
    {
    }
}
