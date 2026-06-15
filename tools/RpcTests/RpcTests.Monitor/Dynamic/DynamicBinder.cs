// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using DynamicExpresso;

namespace Nethermind.RpcTests.Monitor.Dynamic;

/// <summary>
/// Extends DynamicExpresso <see cref="Interpreter"/> with support for binding <typeparamref name="TContext"/> properties and methods.
/// </summary>
// TODO: simplify usage
// ReSharper disable StaticMemberInGenericType - intended
internal static class DynamicBinder<TContext>
{
    private static readonly PropertyInfo[] _props =
    [
        .. typeof(TContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(static p => p.CanRead)
    ];

    private static readonly (string Name, Type DelegateType, Func<TContext, Delegate> Binder)[] _methodBinders =
    [
        .. typeof(TContext).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(static m => !m.IsSpecialName && m.DeclaringType == typeof(TContext) && m.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .Select(CreateInstanceBinder)
    ];

    private static readonly Func<TContext, object>[] _getters = [.. _props.Select(MakeGetter)];

    private static readonly (string Name, Delegate Method)[] _staticMethods =
    [
        .. typeof(TContext).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(static m => !m.IsSpecialName)
            .Select(static m => (m.Name, CreateStaticDelegate(m)))
    ];

    public static readonly Parameter[] Parameters =
    [
        .. _props.Select(static p => new Parameter(p.Name, p.PropertyType)),
        .. _methodBinders.Select(static b => new Parameter(b.Name, b.DelegateType))
    ];

    public static Interpreter CreateInterpreter()
    {
        Interpreter interpreter = new();

        foreach ((string name, Delegate method) in _staticMethods)
            interpreter.SetVariable(name, method, method.GetType());

        return interpreter;
    }

    public static object[] GetArgs(TContext value)
    {
        int i = 0;
        object[] args = new object[_getters.Length + _methodBinders.Length];

        for (int j = 0; j < _getters.Length; j++)
            args[i++] = _getters[j](value);
        for (int j = 0; j < _methodBinders.Length; j++)
            args[i++] = _methodBinders[j].Binder(value);

        return args;
    }

    private static Func<TContext, object> MakeGetter(PropertyInfo prop)
    {
        ParameterExpression param = Expression.Parameter(typeof(TContext));
        Expression instance = prop.GetMethod!.IsStatic ? null! : param;
        UnaryExpression body = Expression.Convert(Expression.Property(instance, prop), typeof(object));
        return Expression.Lambda<Func<TContext, object>>(body, param).Compile();
    }

    private static Delegate CreateStaticDelegate(MethodInfo method)
    {
        ParameterExpression[] parameters = [.. method.GetParameters()
            .Select(static p => Expression.Parameter(p.ParameterType, p.Name))];
        return Expression.Lambda(Expression.Call(method, parameters), parameters).Compile();
    }

    private static (string Name, Type DelegateType, Func<TContext, Delegate> Binder) CreateInstanceBinder(MethodInfo method)
    {
        Type[] paramTypes = [.. method.GetParameters().Select(static p => p.ParameterType)];
        Type delegateType = GetDelegateType(method);

        ParameterExpression ctxParam = Expression.Parameter(typeof(TContext), "ctx");
        ParameterExpression[] methodParams = [.. paramTypes.Select(static (t, i) => Expression.Parameter(t, $"p{i}"))];

        LambdaExpression innerLambda = Expression.Lambda(delegateType,
            Expression.Call(ctxParam, method, methodParams),
            methodParams);

        return (method.Name, delegateType,
            Expression.Lambda<Func<TContext, Delegate>>(
                Expression.Convert(innerLambda, typeof(Delegate)),
                ctxParam).Compile());
    }

    private static Type GetDelegateType(MethodInfo method)
    {
        Type[] paramTypes = [.. method.GetParameters().Select(static p => p.ParameterType)];
        return method.ReturnType == typeof(void)
            ? paramTypes.Length == 0 ? typeof(Action) : Expression.GetActionType(paramTypes)
            : Expression.GetFuncType([.. paramTypes, method.ReturnType]);
    }
}
