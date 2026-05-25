// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public static class ExceptionAssertExtensions
{
    extension(Assert)
    {
        public static void DoesNotThrow<TException>(Action action, string? message = null) where TException : Exception
        {
            ArgumentNullException.ThrowIfNull(action);

            try
            {
                action();
            }
            catch (Exception exception)
            {
                Assert.That(exception, Is.Not.InstanceOf<TException>(), message);
            }
        }
    }
}
