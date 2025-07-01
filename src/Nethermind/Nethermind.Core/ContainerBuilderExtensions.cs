// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using Autofac;
using Autofac.Builder;
using Autofac.Core;
using Autofac.Core.Resolving.Pipeline;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Container;

namespace Nethermind.Core;

/// <summary>
/// DSL to make autofac module a lot cleaner.
/// The template convention is similar to microsoft's IServiceCollection where the TService is the first template argument
/// which make it easy identify which service the line is declaring.
/// </summary>
public static class ContainerBuilderExtensions
{
    public static ContainerBuilder AddSingleton<T>(this ContainerBuilder builder) where T : notnull
    {
        builder.RegisterType<T>()
            .As<T>()
            .CommonNethermindConfig()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddSingleton<T>(this ContainerBuilder builder, T instance) where T : class
    {
        builder.RegisterInstance(instance)
            .As<T>()
            .ExternallyOwned()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddSingletonAsImplementedInterfaces<T>(this ContainerBuilder builder, T instance) where T : class
    {
        builder.RegisterInstance(instance)
            .AsImplementedInterfaces()
            .ExternallyOwned()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddSingleton<T, TArg0>(this ContainerBuilder builder, Func<TArg0, T> factoryMethod) where T : class where TArg0 : notnull
    {
        Func<IComponentContext, TArg0> param0 = CreateArgResolver<TArg0>(factoryMethod.Method, 0);

        builder
            .Register((ctx) => factoryMethod(
                param0(ctx)
            ))
            .As<T>()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddSingleton<T, TArg0, TArg1>(this ContainerBuilder builder, Func<TArg0, TArg1, T> factoryMethod) where T : class where TArg0 : notnull where TArg1 : notnull
    {
        Func<IComponentContext, TArg0> param0 = CreateArgResolver<TArg0>(factoryMethod.Method, 0);
        Func<IComponentContext, TArg1> param1 = CreateArgResolver<TArg1>(factoryMethod.Method, 1);

        builder
            .Register((ctx) => factoryMethod(
                param0(ctx),
                param1(ctx)
            ))
            .As<T>()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddSingleton<T, TArg0, TArg1, TArg2>(this ContainerBuilder builder, Func<TArg0, TArg1, TArg2, T> factoryMethod) where T : class where TArg0 : notnull where TArg1 : notnull where TArg2 : notnull
    {
        Func<IComponentContext, TArg0> param0 = CreateArgResolver<TArg0>(factoryMethod.Method, 0);
        Func<IComponentContext, TArg1> param1 = CreateArgResolver<TArg1>(factoryMethod.Method, 1);
        Func<IComponentContext, TArg2> param2 = CreateArgResolver<TArg2>(factoryMethod.Method, 2);

        builder
            .Register((ctx) => factoryMethod(
                param0(ctx),
                param1(ctx),
                param2(ctx)
            ))
            .As<T>()
            .SingleInstance();

        return builder;
    }


    public static ContainerBuilder AddSingleton<T, TArg0, TArg1, TArg2, TArg3>(this ContainerBuilder builder, Func<TArg0, TArg1, TArg2, TArg3, T> factoryMethod) where T : class where TArg0 : notnull where TArg1 : notnull where TArg2 : notnull where TArg3 : notnull
    {
        Func<IComponentContext, TArg0> param0 = CreateArgResolver<TArg0>(factoryMethod.Method, 0);
        Func<IComponentContext, TArg1> param1 = CreateArgResolver<TArg1>(factoryMethod.Method, 1);
        Func<IComponentContext, TArg2> param2 = CreateArgResolver<TArg2>(factoryMethod.Method, 2);
        Func<IComponentContext, TArg3> param3 = CreateArgResolver<TArg3>(factoryMethod.Method, 3);

        builder
            .Register((ctx) => factoryMethod(
                param0(ctx),
                param1(ctx),
                param2(ctx),
                param3(ctx)
            ))
            .As<T>()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddSingleton<T, TArg0, TArg1, TArg2, TArg3, TArg4>(this ContainerBuilder builder, Func<TArg0, TArg1, TArg2, TArg3, TArg4, T> factoryMethod) where T : class where TArg0 : notnull where TArg1 : notnull where TArg2 : notnull where TArg3 : notnull where TArg4 : notnull
    {
        Func<IComponentContext, TArg0> param0 = CreateArgResolver<TArg0>(factoryMethod.Method, 0);
        Func<IComponentContext, TArg1> param1 = CreateArgResolver<TArg1>(factoryMethod.Method, 1);
        Func<IComponentContext, TArg2> param2 = CreateArgResolver<TArg2>(factoryMethod.Method, 2);
        Func<IComponentContext, TArg3> param3 = CreateArgResolver<TArg3>(factoryMethod.Method, 3);
        Func<IComponentContext, TArg4> param4 = CreateArgResolver<TArg4>(factoryMethod.Method, 4);

        builder
            .Register((ctx) => factoryMethod(
                param0(ctx),
                param1(ctx),
                param2(ctx),
                param3(ctx),
                param4(ctx)
            ))
            .As<T>()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddSingleton<T>(this ContainerBuilder builder, Func<IComponentContext, T> factory) where T : class
    {
        builder.Register(factory)
            .As<T>()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddSingleton<T, TImpl>(this ContainerBuilder builder) where TImpl : T where T : notnull
    {
        builder.RegisterType<TImpl>()
            .As<T>()
            .AsSelf()
            .CommonNethermindConfig()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddKeyedSingleton<T>(this ContainerBuilder builder, object key, T instance) where T : class
    {
        builder.RegisterInstance(instance)
            .Keyed<T>(key)
            .ExternallyOwned()
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddKeyedSingleton<T>(this ContainerBuilder builder, string key, Func<IComponentContext, T> factory) where T : class
    {
        builder.Register(factory)
            .Named<T>(key)
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddScoped<T>(this ContainerBuilder builder) where T : notnull
    {
        builder.RegisterType<T>()
            .As<T>()
            .AsSelf()
            .CommonNethermindConfig()
            .InstancePerLifetimeScope();

        return builder;
    }

    public static ContainerBuilder AddScoped<T>(this ContainerBuilder builder, T instance) where T : class
    {
        builder.Register(ctx => instance)
            .As<T>()
            .AsSelf()
            .ExternallyOwned()
            .InstancePerLifetimeScope();

        return builder;
    }

    public static ContainerBuilder AddScoped<T, TArg0>(this ContainerBuilder builder, Func<TArg0, T> factoryMethod) where T : class where TArg0 : notnull
    {
        Func<IComponentContext, TArg0> param0 = CreateArgResolver<TArg0>(factoryMethod.Method, 0);

        builder
            .Register<T>((ctx) => factoryMethod(
                param0(ctx)
            ))
            .As<T>()
            .AsSelf()
            .InstancePerLifetimeScope();

        return builder;
    }

    public static ContainerBuilder AddScoped<T, TArg0, TArg1>(this ContainerBuilder builder, Func<TArg0, TArg1, T> factoryMethod) where T : class where TArg0 : notnull where TArg1 : notnull
    {
        Func<IComponentContext, TArg0> param0 = CreateArgResolver<TArg0>(factoryMethod.Method, 0);
        Func<IComponentContext, TArg1> param1 = CreateArgResolver<TArg1>(factoryMethod.Method, 1);

        builder
            .Register<T>((ctx) => factoryMethod(
                param0(ctx),
                param1(ctx)
            ))
            .As<T>()
            .AsSelf()
            .InstancePerLifetimeScope();

        return builder;
    }

    public static ContainerBuilder AddScoped<T, TArg0, TArg1, TArg2>(this ContainerBuilder builder, Func<TArg0, TArg1, TArg2, T> factoryMethod) where T : class where TArg0 : notnull where TArg1 : notnull where TArg2 : notnull
    {
        Func<IComponentContext, TArg0> param0 = CreateArgResolver<TArg0>(factoryMethod.Method, 0);
        Func<IComponentContext, TArg1> param1 = CreateArgResolver<TArg1>(factoryMethod.Method, 1);
        Func<IComponentContext, TArg2> param2 = CreateArgResolver<TArg2>(factoryMethod.Method, 2);

        builder
            .Register((ctx) => factoryMethod(
                param0(ctx),
                param1(ctx),
                param2(ctx)
            ))
            .As<T>()
            .AsSelf()
            .InstancePerLifetimeScope();

        return builder;
    }

    public static ContainerBuilder AddScoped<T, TImpl>(this ContainerBuilder builder) where TImpl : T where T : notnull
    {
        builder.RegisterType<TImpl>()
            .As<TImpl>()
            .CommonNethermindConfig()
            .InstancePerLifetimeScope();

        builder.Bind<T, TImpl>();

        return builder;
    }

    public static ContainerBuilder AddScopedOpenGeneric(this ContainerBuilder builder, Type interfaceType, Type implType)
    {
        builder.RegisterGeneric(implType)
            .As(interfaceType)
            .WithAttributeFiltering()
            .InstancePerLifetimeScope();

        return builder;
    }

    public static ContainerBuilder AddKeyedScoped<T, TImpl>(this ContainerBuilder builder, object key) where TImpl : notnull where T : notnull
    {
        builder.RegisterType<TImpl>()
            .Keyed<T>(key)
            .CommonNethermindConfig()
            .InstancePerLifetimeScope();

        return builder;
    }

    public static ContainerBuilder AddScoped<T>(this ContainerBuilder builder, Func<IComponentContext, T> factoryMethod) where T : class
    {
        return builder.AddScoped<T, IComponentContext>(factoryMethod);
    }

    public static ContainerBuilder Add<T>(this ContainerBuilder builder) where T : class
    {
        builder.RegisterType<T>()
            .CommonNethermindConfig()
            .As<T>();

        return builder;
    }

    public static ContainerBuilder Add<T>(this ContainerBuilder builder, Func<IComponentContext, T> factory) where T : class
    {
        builder.Register(factory)
            .As<T>();

        return builder;
    }

    public static ContainerBuilder Add<T, TImpl>(this ContainerBuilder builder) where T : class where TImpl : notnull
    {
        builder.RegisterType<TImpl>()
            .CommonNethermindConfig()
            .As<T>();

        return builder;
    }

    public static ContainerBuilder AddAdvance<T>(this ContainerBuilder builder, Action<IRegistrationBuilder<T, ConcreteReflectionActivatorData, SingleRegistrationStyle>> configurer) where T : class
    {
        IRegistrationBuilder<T, ConcreteReflectionActivatorData, SingleRegistrationStyle> adv = builder
            .RegisterType<T>()
            .CommonNethermindConfig();

        configurer(adv);

        return builder;
    }

    public static ContainerBuilder AddComposite<T, TComposite>(this ContainerBuilder builder) where T : class where TComposite : T
    {
        builder.RegisterComposite<TComposite, T>();

        return builder;
    }

    public static ContainerBuilder AddDecorator<T, TDecorator>(this ContainerBuilder builder) where T : class where TDecorator : T
    {
        builder.RegisterDecorator<TDecorator, T>();

        return builder;
    }

    public static ContainerBuilder AddDecorator<T>(this ContainerBuilder builder, Func<IComponentContext, T, T> decoratorFunc) where T : class
    {
        builder.RegisterDecorator<T>((ctx, _param, before) => decoratorFunc(ctx, before));

        return builder;
    }

    /// <summary>
    /// A convenient way of creating a service whose member can be configured indipendent of other instance of the same
    /// type (assuming the type is of lifetime scope). This is useful for same type with multiple configuration
    /// or a graph of multiple same type. The T is expected to be of a main container of sort that contains the
    /// main service of interest.
    /// Note: The T should dispose an injected ILifetimeScope on dispose as ILifetimeScope is not automatically disposed
    /// when parent scope is disposed.
    /// </summary>
    public static ContainerBuilder RegisterNamedComponentInItsOwnLifetime<T>(this ContainerBuilder builder, string name, Action<ContainerBuilder> configurator) where T : notnull
    {
        builder.Register<ILifetimeScope, T>(ctx => ctx.BeginLifetimeScope(configurator).Resolve<T>())
            .Named<T>(name)
            .SingleInstance();

        return builder;
    }

    public static ContainerBuilder AddModule(this ContainerBuilder builder, IModule module)
    {
        builder.RegisterModule(module);

        return builder;
    }

    public static ContainerBuilder AddSource(this ContainerBuilder builder, IRegistrationSource registrationSource)
    {
        builder.RegisterSource(registrationSource);

        return builder;
    }

    public static ContainerBuilder Map<TTo, TFrom>(this ContainerBuilder builder, Func<TFrom, TTo> mapper) where TFrom : notnull where TTo : notnull
    {
        builder.Register(mapper)
            .As<TTo>()
            .ExternallyOwned();

        return builder;
    }

    public static ContainerBuilder OnBuild(this ContainerBuilder builder, Action<ILifetimeScope> action)
    {
        builder.RegisterBuildCallback(action);

        return builder;
    }

    public static ContainerBuilder Bind<TTo, TFrom>(this ContainerBuilder builder) where TFrom : TTo where TTo : notnull
    {
        builder.Register(static (it) => it.Resolve<TFrom>())
            .As<TTo>()
            .ExternallyOwned();

        return builder;
    }

    public static ContainerBuilder OnActivate<TService>(this ContainerBuilder builder, Action<TService, ResolveRequestContext> action) where TService : class
    {
        builder
            .RegisterServiceMiddleware<TService>(PipelinePhase.ServicePipelineEnd, (ctx, act) =>
            {
                act(ctx);
                // At this point, it should has been resolved
                action(((TService)ctx.Instance!), ctx);
            });

        return builder;
    }

    /// <summary>
    /// Resolve `TResolve` when `TService` is also resolved. Used for when `TResolve` need to do something
    /// as a side effect, probably with some event to `TService`.
    /// Note: If `TResolve` depends on `TService` indirectly, there will be a stack overflow. Specify a direct
    /// `TService` to avoid that.
    /// </summary>
    /// <param name="builder"></param>
    /// <typeparam name="TResolve"></typeparam>
    /// <typeparam name="TService"></typeparam>
    /// <returns></returns>
    public static ContainerBuilder ResolveOnServiceActivation<TResolve, TService>(this ContainerBuilder builder) where TService : class where TResolve : notnull
    {
        builder
            .OnActivate<TService>((service, ctx) =>
            {
                ctx.ActivationScope.Resolve<TResolve>(
                    ctx.Parameters
                        .Concat([TypedParameter.From((TService)service!)])
                );
            });

        return builder;
    }

    private static IRegistrationBuilder<TLimit, TReflectionActivatorData, TRegistrationStyle> CommonNethermindConfig<TLimit, TReflectionActivatorData, TRegistrationStyle>(
        this IRegistrationBuilder<TLimit, TReflectionActivatorData, TRegistrationStyle> builder
    ) where TReflectionActivatorData : ReflectionActivatorData
    {
        return builder
            .WithAttributeFiltering()
            .FindConstructorsWith(NethermindConstructorFinder.Instance);
    }

    private static Func<IComponentContext, T> CreateArgResolver<T>(MethodInfo methodInfo, int paramIndex) where T : notnull
    {
        if (methodInfo.GetParameters()[paramIndex].GetCustomAttribute<KeyFilterAttribute>() is { } keyFilter)
        {
            return (ctx) => ctx.ResolveKeyed<T>(keyFilter.Key);
        }
        return (ctx) => ctx.Resolve<T>();
    }
}

/// <summary>
/// Mark a property so that it is not picked up by `AddPropertiesFrom`.
/// </summary>
public class SkipServiceCollectionAttribute : Attribute
{
}
