// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Autofac.Core;
using Autofac.Core.Activators.Delegate;
using Autofac.Core.Lifetime;
using Autofac.Core.Registration;

namespace Nethermind.Config;

/// <summary>
/// Dynamically resolve IConfig<T>
/// </summary>
public class ConfigRegistrationSource : IRegistrationSource
{
    public IEnumerable<IComponentRegistration> RegistrationsFor(Service service, Func<Service, IEnumerable<ServiceRegistration>> registrationAccessor)
    {
        IServiceWithType swt = service as IServiceWithType;
        if (swt == null || !typeof(IConfig).IsAssignableFrom(swt.ServiceType))
        {
            // It's not a request for the base handler type, so skip it.
            return Enumerable.Empty<IComponentRegistration>();
        }

        // Dynamically resolve IConfig
        ComponentRegistration registration = new ComponentRegistration(
            Guid.NewGuid(),
            new DelegateActivator(swt.ServiceType, (c, p) =>
            {
                IConfigProvider configProvider = c.Resolve<IConfigProvider>();
                return configProvider.GetConfig(swt.ServiceType);
            }),
            new RootScopeLifetime(),
            InstanceSharing.Shared,
            InstanceOwnership.OwnedByLifetimeScope,
            new[] { service },
            new Dictionary<string, object>());

        return new IComponentRegistration[] { registration };
    }

    public bool IsAdapterForIndividualComponents => false;
}
