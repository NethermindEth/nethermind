// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autofac;
using Autofac.Builder;
using Autofac.Core;
using Nethermind.Core.Exceptions;

namespace Nethermind.Core.Container;

/// <summary>
/// If a service does not have any registration, try to get it from a member fo a type `T`.
/// This allow fetching of unregistered/unconfigured component from `NethermindApi`.
/// </summary>
public class FallbackToFieldFromApi<TApi> : IRegistrationSource where TApi : notnull
{
    private readonly Dictionary<Type, PropertyInfo> _availableTypes;

    public FallbackToFieldFromApi()
    {
        Type tApi = typeof(TApi);

        IEnumerable<PropertyInfo> properties = tApi
            .GetInterfaces()
            .SelectMany(i => i.GetProperties(BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public))
            .Where(p => p.GetCustomAttribute<SkipServiceCollectionAttribute>() == null);

        Dictionary<Type, PropertyInfo> availableTypes = new Dictionary<Type, PropertyInfo>();

        foreach (PropertyInfo propertyInfo in properties)
        {
            availableTypes[propertyInfo.PropertyType] = propertyInfo;
        }

        _availableTypes = availableTypes;
    }

    public IEnumerable<IComponentRegistration> RegistrationsFor(Service service, Func<Service, IEnumerable<ServiceRegistration>> registrationAccessor)
    {
        if (registrationAccessor == null)
        {
            throw new ArgumentNullException(nameof(registrationAccessor));
        }

        IServiceWithType? ts = service as IServiceWithType;
        if (ts == null || ts.ServiceType == typeof(string))
        {
            return Enumerable.Empty<IComponentRegistration>();
        }

        PropertyInfo? property;
        Type serviceType = ts.ServiceType;
        if (registrationAccessor(service).Any())
        {
            // Already have registration
            if (_availableTypes.TryGetValue(serviceType, out property) && property.SetMethod != null)
            {
                // To prevent mistake, a service that already have registration via dependency injection must not also
                // have a setter in api. This is to prevent the assumption that the setter will caause the service
                // to be reflected in dependency injected components. It will not. Please remove the setter from the
                // api.
                throw new InvalidConfigurationException($"Service {serviceType} has both container registration and mutable field in {typeof(TApi).Name}", -1);
            }

            return Enumerable.Empty<IComponentRegistration>();
        }

        if (!_availableTypes.TryGetValue(serviceType, out property))
        {
            // Not available as a property
            return Enumerable.Empty<IComponentRegistration>();
        }

        ComponentKeyAttribute? keyAttribute = property.GetCustomAttribute<ComponentKeyAttribute>();
        if (keyAttribute is not null)
        {
            if (ts is not KeyedService keyedService)
            {
                // not a keyed service
                return Enumerable.Empty<IComponentRegistration>();
            }

            if (!keyedService.ServiceKey.Equals(keyAttribute.Key))
            {
                // Different key
                return Enumerable.Empty<IComponentRegistration>();
            }
        }

        IRegistrationBuilder<object, SimpleActivatorData, SingleRegistrationStyle> builder = RegistrationBuilder.ForDelegate(serviceType, (ctx, reg) =>
        {
            TApi baseT = ctx.Resolve<TApi>();
            return property.GetValue(baseT)!;
        });

        if (keyAttribute is not null)
        {
            return new[] { builder.Keyed(keyAttribute.Key, serviceType).CreateRegistration() };
        }

        return new[] { builder.CreateRegistration() };
    }

    public bool IsAdapterForIndividualComponents => false;
}
