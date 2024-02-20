// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Autofac;
using Autofac.Builder;
using Autofac.Core;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.Runner.Modules;

public static class RegistrationBuilderExtensions
{
    /// <summary>
    /// Convenient utility to register singleton. Note, singleton are not really recommended as it make reusability
    /// a bit hard. Try not to use this if the class does not have any events or state.
    /// </summary>
    /// <param name="builder"></param>
    /// <typeparam name="TImpl"></typeparam>
    /// <typeparam name="TService"></typeparam>
    public static void RegisterSingleton<TImpl, TService>(this ContainerBuilder builder)
    {
        IRegistrationBuilder<TImpl, ConcreteReflectionActivatorData, SingleRegistrationStyle>? registration =
            builder.RegisterType<TImpl>();

        // AutoFac does not enable `WithAttributeFiltering` by default for performance reason which is very inconvenient.
        // So we'll look at the implementation's constructor to see if any of it has the filter attribute, if so
        // it is enabled.
        bool hasAttributeFilter = false;
        foreach (ConstructorInfo constructorInfo in typeof(TImpl).GetConstructors())
        {
            foreach (ParameterInfo parameterInfo in constructorInfo.GetParameters())
            {
                if (parameterInfo.GetCustomAttribute<KeyFilterAttribute>() != null)
                {
                    hasAttributeFilter = true;
                    break;
                }
            }

            if (hasAttributeFilter) break;
        }

        if (hasAttributeFilter)
        {
            registration = registration.WithAttributeFiltering();
        }

        registration
            .SingleInstance()
            .As<TService>();
    }

    /// <summary>
    /// Convenient method for registering implementation
    /// </summary>
    /// <param name="builder"></param>
    /// <typeparam name="TImpl"></typeparam>
    /// <typeparam name="TService"></typeparam>
    public static void RegisterImpl<TImpl, TService>(this ContainerBuilder builder)
    {
        IRegistrationBuilder<TImpl, ConcreteReflectionActivatorData, SingleRegistrationStyle>? registration =
            builder.RegisterType<TImpl>();

        // AutoFac does not enable `WithAttributeFiltering` by default for performance reason which is very inconvenient.
        // So we'll look at the implementation's constructor to see if any of it has the filter attribute, if so
        // it is enabled.
        bool hasAttributeFilter = false;
        foreach (ConstructorInfo constructorInfo in typeof(TImpl).GetConstructors())
        {
            foreach (ParameterInfo parameterInfo in constructorInfo.GetParameters())
            {
                if (parameterInfo.GetCustomAttribute<KeyFilterAttribute>() != null)
                {
                    hasAttributeFilter = true;
                    break;
                }
            }

            if (hasAttributeFilter) break;
        }

        if (hasAttributeFilter)
        {
            registration = registration.WithAttributeFiltering();
        }

        registration
            .As<TService>();
    }


    public static void RegisterKeyedMapping<TSource, TType>(this ContainerBuilder builder, ComponentKey key, Func<TSource, TType> mapper)
    {
        builder.Register(mapper).Keyed<TType>(key);
    }
}

public static class GetParameter
{
    private class KeyedParameterResolver<T> : Parameter
    {
        private readonly ParameterKey _key;
        private readonly Func<T, object> _resolver;

        public KeyedParameterResolver(ParameterKey key, Func<T, object> resolver)
        {
            _key = key;
            _resolver = resolver;
        }

        public override bool CanSupplyValue(ParameterInfo pi, IComponentContext context, out Func<object?>? valueProvider)
        {
            if (pi.GetCustomAttribute<KeyedParameterAttribute>() is KeyedParameterAttribute attribute && attribute.Key == _key)
            {
                valueProvider = () => _resolver(context.Resolve<T>());

                return true;
            }

            valueProvider = null;
            return false;
        }
    }

    public static Parameter FromType<T>(ParameterKey key, Func<T, object> mapper)
    {
        return new KeyedParameterResolver<T>(key, mapper);
    }
}
