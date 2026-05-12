// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public static class ExceptionAssertionExtensions
{
    public static void AssertDoesNotThrowExceptionOfType<TException>(Action code, string? message = null)
        where TException : Exception
    {
        try
        {
            code();
        }
        catch (TException exception)
        {
            Assert.Fail(message ?? $"Expected no {typeof(TException).Name}, but got: {exception}");
        }
        catch
        {
            // FluentAssertions NotThrow<TException> ignored other exception types.
        }
    }
}
