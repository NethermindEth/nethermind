// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public class InvalidBlockHashException : InvalidBlockException
{
    public InvalidBlockHashException(Block suggestedBlock, string message, Exception? innerException = null)
        : base(suggestedBlock, message, innerException) { }
}
