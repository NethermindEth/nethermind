// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Autofac.Core;
using Autofac.Core.Activators.Delegate;
using Autofac.Core.Lifetime;
using Autofac.Core.Registration;

namespace Nethermind.Core.Container;

/// <summary>
/// Utility between two type that act on keyed service.
/// </summary>
/// <param name="mapper"></param>
/// <typeparam name="TFrom"></typeparam>
/// <typeparam name="TTo"></typeparam>
public class KeyedMapperRegistrationSource<TFrom, TTo>(Func<TFrom, TTo> mapper) : IRegistrationSource where TFrom : notnull
{
    public IEnumerable<IComponentRegistration> RegistrationsFor(Service service, Func<Service, IEnumerable<ServiceRegistration>> registrationAccessor)
    {
        if (service is not KeyedService keyedService || keyedService.ServiceType != typeof(TTo))
        {
            // Not a keyed service
            return Enumerable.Empty<IComponentRegistration>();
        }

        ComponentRegistration registration = new ComponentRegistration(
            Guid.NewGuid(),
            new DelegateActivator(keyedService.ServiceType, (c, p) =>
            {
                TFrom from = c.ResolveKeyed<TFrom>(keyedService.ServiceKey);
                return mapper(from)!;
            }),
            new RootScopeLifetime(),
            InstanceSharing.Shared,
            InstanceOwnership.OwnedByLifetimeScope,
            new[] { service },
            new Dictionary<string, object?>());

        return [registration];
    }

    public bool IsAdapterForIndividualComponents => true;
}
