// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nethermind.Init.Steps
{
    public class StepDependencyException : Exception
    {
        public StepDependencyException()
        {
        }

        public StepDependencyException(string message)
            : base(message)
        {
        }

        public static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is not null)
                return;
            throw new StepDependencyException(paramName ?? "");
        }
    }
}
