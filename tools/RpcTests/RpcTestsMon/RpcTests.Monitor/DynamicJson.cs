// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicExpresso;

namespace Nethermind.RpcTests.Monitor;

// ReSharper disable StaticMemberInGenericType - intended
internal class DynamicJson<TContext>
{
    private static readonly PropertyInfo[] _props = [.. typeof(TContext).GetProperties().Where(static p => p.CanRead)];

    private static readonly (string Name, Type DelegateType, Func<TContext, Delegate> Binder)[] _instanceMethodBinders =
    [
        .. typeof(TContext).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(static m => !m.IsSpecialName && m.DeclaringType == typeof(TContext) && m.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .Select(CreateInstanceBinder)
    ];

    private static readonly Parameter[] _parameters =
    [
        .. _props.Select(static p => new Parameter(p.Name, p.PropertyType)),
        .. _instanceMethodBinders.Select(static b => new Parameter(b.Name, b.DelegateType))
    ];

    private static readonly Func<TContext, object>[] _getters =
    [
        .. _props.Select(MakeGetter)
    ];

    private static readonly (string Name, Delegate Method)[] _staticMethods =
    [
        .. typeof(TContext).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(static m => !m.IsSpecialName)
            .Select(static m => (m.Name, CreateStaticDelegate(m)))
    ];

    private readonly JsonNode _template;
    private readonly List<(object[] Path, Lambda Expression)> _expressions = [];

    public DynamicJson(JsonNode template)
    {
        _template = template.DeepClone();

        Interpreter interpreter = new();
        foreach ((string name, Delegate method) in _staticMethods)
            interpreter.SetVariable(name, method);
        Scan(template, [], interpreter);
    }

    public JsonNode? Compile(TContext value)
    {
        object[] args = new object[_getters.Length + _instanceMethodBinders.Length];
        for (int i = 0; i < _getters.Length; i++)
            args[i] = _getters[i](value);
        for (int i = 0; i < _instanceMethodBinders.Length; i++)
            args[_getters.Length + i] = _instanceMethodBinders[i].Binder(value);

        JsonNode result = _template.DeepClone();

        foreach ((object[] path, Lambda expr) in _expressions)
        {
            JsonNode? node = JsonSerializer.SerializeToNode(expr.Invoke(args));
            if (path.Length == 0)
                return node;

            JsonNode? parent = Navigate(result, path);
            if (path[^1] is string lastKey)
                ((JsonObject)parent!)[lastKey] = node;
            else
                ((JsonArray)parent!)[(int)path[^1]] = node;
        }

        return result;
    }

    private static JsonNode? Navigate(JsonNode? node, object[] path)
    {
        for (int i = 0; i < path.Length - 1; i++)
            node = path[i] is string key ? node?[key] : node?[(int)path[i]];
        return node;
    }

    private void Scan(JsonNode? node, List<object> path, Interpreter interpreter)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? value) in obj)
                {
                    path.Add(key);
                    Scan(value, path, interpreter);
                    path.RemoveAt(path.Count - 1);
                }
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    path.Add(i);
                    Scan(arr[i], path, interpreter);
                    path.RemoveAt(path.Count - 1);
                }
                break;
            case JsonValue val when val.TryGetValue(out string? str) && str.StartsWith("{{") && str.EndsWith("}}"):
                _expressions.Add((path.ToArray(), interpreter.Parse(str[2..^2], _parameters)));
                break;
        }
    }

    private static Func<TContext, object> MakeGetter(PropertyInfo prop)
    {
        ParameterExpression param = Expression.Parameter(typeof(TContext));
        UnaryExpression body = Expression.Convert(Expression.Property(param, prop), typeof(object));
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
