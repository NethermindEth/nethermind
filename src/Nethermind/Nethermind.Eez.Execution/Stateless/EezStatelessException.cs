// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Eez.Execution.Stateless;

public sealed class EezStatelessException(EezStatelessFailure failure, string message) : Exception(message)
{
    public EezStatelessFailure Failure { get; } = failure;
}
