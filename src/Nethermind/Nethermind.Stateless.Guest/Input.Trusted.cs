// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Stateless.Execution;

namespace Nethermind.Stateless.Guest;

partial class Program
{
    /// <inheritdoc cref="StatelessExecutor.ExecuteTrusted(ReadOnlySpan{byte})"/>
    private static partial ReadOnlySpan<byte> Execute(ReadOnlySpan<byte> input) => StatelessExecutor.ExecuteTrusted(input);
}
