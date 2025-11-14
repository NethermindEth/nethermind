// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public static class NSubstituteExtensions
{
    /// <summary>
    /// Checks if a substitute received matching calls without throwing exceptions.
    /// Suitable for polling scenarios with Is.True.After().
    /// </summary>
    public static bool ReceivedCallsMatching<T>(
        this T substitute,
        Action<T> action,
        int requiredNumberOfCalls = 1,
        int? maxNumberOfCalls = null,
        [CallerArgumentExpression(nameof(action))] string? expression = null) where T : class
    {
        if (maxNumberOfCalls < requiredNumberOfCalls) throw new ArgumentException($"{nameof(maxNumberOfCalls)} must be greater than or equal to {nameof(requiredNumberOfCalls)}", nameof(maxNumberOfCalls));
        maxNumberOfCalls ??= requiredNumberOfCalls;
        ISubstitutionContext context = SubstitutionContext.Current;
        ICallRouter callRouter = context.GetCallRouterFor(substitute);

        // Set up the call specification by invoking the action
        action(substitute);

        // Get the pending specification that was just set up
        IPendingSpecification pendingSpec = context.ThreadContext.PendingSpecification;
        if (!pendingSpec.HasPendingCallSpecInfo()) return false;

        // Use a query to check if the call was received
        PendingSpecificationInfo? callSpecInfo = context.ThreadContext.PendingSpecification.UseCallSpecInfo();
        int? matchCount = callSpecInfo?.Handle(
            // Lambda 1: Handle call specification with Arg matchers
            callSpec => callRouter.ReceivedCalls().Where(callSpec.IsSatisfiedBy).Count(),
            // Lambda 2: Handle matching with concrete argument values
            GetMatchCount);

        return matchCount.HasValue && CheckMatchCount(matchCount.Value);

        bool CheckMatchCount(int count) => count >= requiredNumberOfCalls && count <= maxNumberOfCalls;

        int GetMatchCount(ICall expectedCall)
        {
            IEnumerable<ICall> receivedCalls = callRouter.ReceivedCalls();
            MethodInfo expectedMethod = expectedCall.GetMethodInfo();
            object?[] expectedArgs = expectedCall.GetArguments();

            int matchCount = 0;
            foreach (ICall call in receivedCalls)
            {
                // Match method name and arguments
                if (call.GetMethodInfo() == expectedMethod)
                {
                    object?[] callArgs = call.GetArguments();
                    matchCount += expectedArgs.SequenceEqual(callArgs) ? 1 : 0;
                }
            }

            return matchCount;
        }
    }
}
