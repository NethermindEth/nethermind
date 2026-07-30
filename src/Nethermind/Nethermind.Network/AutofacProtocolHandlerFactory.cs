// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Autofac;
using Autofac.Core;
using Autofac.Features.AttributeFilters;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;

namespace Nethermind.Network;

/// <summary>
/// Creates protocol handlers with a cached constructor activator.
/// </summary>
/// <remarks>
/// Non-<see cref="ISession"/> constructor dependencies of <typeparamref name="THandler"/>
/// are resolved once and reused across all sessions; protocol handlers registered through this
/// factory must only depend on singleton-safe services outside the session parameter.
/// </remarks>
internal sealed class AutofacProtocolHandlerFactory<THandler>(
    ILifetimeScope lifetimeScope,
    string protocolCode,
    int? expectedVersion = null) : IProtocolHandlerFactory
    where THandler : IProtocolHandler
{
    private static readonly ConstructorInfo Constructor = SelectConstructor();
    private static readonly ParameterInfo[] ConstructorParameters = Constructor.GetParameters();
    private static readonly Func<ISession, object?[], THandler> Activator = CompileActivator();
    private static readonly int DependencyCount = CountDependencies();

    private readonly Lock _lock = new();
    private object?[]? _dependencies;

    public string ProtocolCode => protocolCode;

    public bool TryCreate(ISession session, int version, [NotNullWhen(true)] out IProtocolHandler? handler)
    {
        if (expectedVersion is not null && version != expectedVersion)
        {
            handler = null;
            return false;
        }

        handler = Activator(session, GetDependencies());
        return true;
    }

    private object?[] GetDependencies()
    {
        object?[]? dependencies = Volatile.Read(ref _dependencies);
        if (dependencies is not null)
        {
            return dependencies;
        }

        lock (_lock)
        {
            dependencies = _dependencies;
            if (dependencies is null)
            {
                dependencies = ResolveDependencies(lifetimeScope);
                Volatile.Write(ref _dependencies, dependencies);
            }

            return dependencies;
        }
    }

    private static object?[] ResolveDependencies(IComponentContext context)
    {
        object?[] dependencies = new object?[DependencyCount];
        int dependencyIndex = 0;
        for (int i = 0; i < ConstructorParameters.Length; i++)
        {
            ParameterInfo parameter = ConstructorParameters[i];
            if (parameter.ParameterType == typeof(ISession))
            {
                continue;
            }

            dependencies[dependencyIndex++] = ResolveDependency(context, parameter);
        }

        return dependencies;
    }

    private static object? ResolveDependency(IComponentContext context, ParameterInfo parameter)
    {
        Type parameterType = parameter.ParameterType;
        if (parameter.GetCustomAttribute<KeyFilterAttribute>() is { } keyFilter)
        {
            KeyedService keyedService = new(keyFilter.Key, parameterType);
            if (context.ComponentRegistry.IsRegistered(keyedService))
            {
                return context.ResolveKeyed(keyFilter.Key, parameterType);
            }

            throw new InvalidOperationException(
                $"Missing keyed registration for {parameterType.Name} with key '{keyFilter.Key}' " +
                $"required by {typeof(THandler).Name}.{Constructor.Name} parameter '{parameter.Name}'.");
        }
        else if (context.ComponentRegistry.IsRegistered(new TypedService(parameterType)))
        {
            return context.Resolve(parameterType);
        }

        if (parameter.HasDefaultValue)
        {
            return parameter.DefaultValue;
        }

        return context.Resolve(parameterType);
    }

    private static Func<ISession, object?[], THandler> CompileActivator()
    {
        ParameterExpression sessionParameter = Expression.Parameter(typeof(ISession), "session");
        ParameterExpression dependenciesParameter = Expression.Parameter(typeof(object[]), "dependencies");

        Expression[] arguments = new Expression[ConstructorParameters.Length];
        int dependencyIndex = 0;
        for (int i = 0; i < ConstructorParameters.Length; i++)
        {
            Type parameterType = ConstructorParameters[i].ParameterType;
            arguments[i] = parameterType == typeof(ISession)
                ? sessionParameter
                : Expression.Convert(
                    Expression.ArrayIndex(dependenciesParameter, Expression.Constant(dependencyIndex++)),
                    parameterType);
        }

        NewExpression constructorCall = Expression.New(Constructor, arguments);
        return Expression.Lambda<Func<ISession, object?[], THandler>>(
                constructorCall,
                sessionParameter,
                dependenciesParameter)
            .Compile();
    }

    private static ConstructorInfo SelectConstructor()
    {
        ConstructorInfo? selected = null;
        int selectedParameterCount = -1;
        foreach (ConstructorInfo constructor in typeof(THandler).GetConstructors(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (constructor.IsPrivate || CountSessionParameters(constructor) != 1)
            {
                continue;
            }

            int parameterCount = constructor.GetParameters().Length;
            if (parameterCount > selectedParameterCount)
            {
                selected = constructor;
                selectedParameterCount = parameterCount;
            }
        }

        return selected ?? throw new InvalidOperationException(
            $"{typeof(THandler).Name} must have a constructor with exactly one {nameof(ISession)} parameter.");
    }

    private static int CountSessionParameters(ConstructorInfo constructor)
    {
        int count = 0;
        ParameterInfo[] parameters = constructor.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType == typeof(ISession))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountDependencies()
    {
        int count = 0;
        for (int i = 0; i < ConstructorParameters.Length; i++)
        {
            if (ConstructorParameters[i].ParameterType != typeof(ISession))
            {
                count++;
            }
        }

        return count;
    }
}
