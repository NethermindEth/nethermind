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
/// If a service does not have any registration, try to get it from a member of a type `T`.
/// This allow fetching of unregistered/unconfigured component from `NethermindApi`.
/// </summary>
public class FallbackToFieldFromApi<TApi> : IRegistrationSource where TApi : notnull
{
    private readonly Dictionary<Type, PropertyInfo> _availableTypes;
    private readonly bool _allowRedundantRegistration;

    public FallbackToFieldFromApi(bool directlyDeclaredOnly = true, bool allowRedundantRegistration = false)
    {
        _allowRedundantRegistration = allowRedundantRegistration;

        Type tApi = typeof(TApi);

        BindingFlags flag = BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.Public;
        if (directlyDeclaredOnly)
            flag |= BindingFlags.DeclaredOnly;

        IEnumerable<PropertyInfo> properties = tApi
            .GetProperties(flag)
            .Where(p => p.GetCustomAttribute<SkipServiceCollectionAttribute>() is null);

        Dictionary<Type, PropertyInfo> availableTypes = new Dictionary<Type, PropertyInfo>();

        foreach (PropertyInfo propertyInfo in properties)
        {
            availableTypes[propertyInfo.PropertyType] = propertyInfo;
        }

        _availableTypes = availableTypes;
    }

    public IEnumerable<IComponentRegistration> RegistrationsFor(Service service, Func<Service, IEnumerable<ServiceRegistration>> registrationAccessor)
    {
        if (registrationAccessor is null)
        {
            throw new ArgumentNullException(nameof(registrationAccessor));
        }

        IServiceWithType? ts = service as IServiceWithType;
        if (ts is null || ts.ServiceType == typeof(string))
        {
            return Enumerable.Empty<IComponentRegistration>();
        }

        PropertyInfo? property;
        Type serviceType = ts.ServiceType;
        if (registrationAccessor(service).Any(reg => !reg.Registration.Metadata.ContainsKey(FallbackMetadata)))
        {
            // Already have registration
            if (!_allowRedundantRegistration && _availableTypes.TryGetValue(serviceType, out property) && property.SetMethod is not null)
            {
                // To prevent mistake, a service that already have registration via dependency injection must not also
                // have a setter in api. This is to prevent the assumption that the setter will cause the service
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

        IRegistrationBuilder<object, SimpleActivatorData, SingleRegistrationStyle> builder = RegistrationBuilder.ForDelegate(serviceType, (ctx, reg) =>
        {
            TApi baseT = ctx.Resolve<TApi>();
            object? value = property.GetValue(baseT);
            if (value is null)
            {
                throw new MissingFieldException($"Property {property.Name} in {baseT.GetType().Name} is null");
            }
            return value!;
        })
            .WithMetadata(FallbackMetadata, true)
            .ExternallyOwned();

        return new[] { builder.CreateRegistration() };
    }

    public bool IsAdapterForIndividualComponents => false;

    public static string FallbackMetadata = "IsFallback";
}
