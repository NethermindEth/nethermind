// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using NSubstitute.Core;
using NSubstitute.Exceptions;
using NSubstitute.ReceivedExtensions;
using NSubstitute.Routing;

namespace Nethermind.Core.Test;

public static class NSubstituteExtensions
{
    /// <summary>
    /// Its like standard `Received` except it will retry for up to 5 second before finally throwing.
    /// </summary>
    /// <param name="substitute"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="NullSubstituteReferenceException"></exception>
    public static T EventuallyReceived<T>(this T substitute)
    {
        if (substitute == null) throw new NullSubstituteReferenceException();

        var context = SubstitutionContext.Current;
        var callRouter = context.GetCallRouterFor(substitute);

        context.ThreadContext.SetNextRoute(callRouter,
            x => new EventuallyRoute(context.RouteFactory.CheckReceivedCalls(x, MatchArgs.AsSpecifiedInCall, Quantity.AtLeastOne())));
        return substitute;
    }

    class EventuallyRoute : IRoute
    {
        private IRoute _baseRoute;

        public EventuallyRoute(IRoute baseRoute)
        {
            _baseRoute = baseRoute;
        }
        public object Handle(ICall call)
        {
            DateTimeOffset deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(5);
            while (true)
            {
                try
                {
                    return _baseRoute.Handle(call);
                }
                catch
                {
                    if (DateTimeOffset.Now > deadline) throw;
                    Task.Delay(TimeSpan.FromMilliseconds(50)).Wait();
                }
            }
        }
    }
}
