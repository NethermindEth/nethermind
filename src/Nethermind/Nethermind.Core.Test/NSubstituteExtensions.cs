// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NSubstitute;

namespace Nethermind.Core.Test;

public static class NSubstituteExtensions
{
    public static bool ReceivedBool<T>(this T substitute, Action<T> action, int requiredNumberOfCalls = 1) where T : class
    {
        try
        {
            action(substitute.Received(requiredNumberOfCalls));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool DidNotReceiveBool<T>(this T substitute, Action<T> action) where T : class
    {
        try
        {
            action(substitute.DidNotReceive());
            return true;
        }
        catch
        {
            return false;
        }
    }
}
