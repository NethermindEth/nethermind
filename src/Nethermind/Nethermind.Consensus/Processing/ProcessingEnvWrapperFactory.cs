// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using Autofac;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Generates and caches, once per wrapper interface, a concrete class that surfaces an Autofac
/// <see cref="ILifetimeScope"/> as that interface: each read-only property is resolved from the scope into
/// a backing field and returned by its getter, each method inherited from a base interface is forwarded to
/// that interface resolved from the scope, and <see cref="IDisposable.Dispose"/> /
/// <see cref="IAsyncDisposable.DisposeAsync"/> dispose the scope.
/// </summary>
/// <remarks>
/// The generated type lives in a dynamic assembly, so a wrapper interface must be <b>public</b>, and it
/// must implement <see cref="IDisposable"/> and/or <see cref="IAsyncDisposable"/> so the scope is released.
/// A non-getter method declared directly on the wrapper interface has no component to forward to and is
/// rejected when the wrapper is built. Components and forwarding targets are resolved eagerly at
/// construction, so any missing registration surfaces then.
/// </remarks>
internal static class ProcessingEnvWrapperFactory
{
    private static readonly ModuleBuilder Module =
        AssemblyBuilder
            .DefineDynamicAssembly(new AssemblyName("Nethermind.Consensus.Processing.Generated"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Main");

    private static readonly ConcurrentDictionary<Type, Func<ILifetimeScope, object>> Factories = new();
    private static int _typeCounter;

    private static readonly MethodInfo ResolveMethod =
        typeof(ResolutionExtensions).GetMethod(nameof(ResolutionExtensions.Resolve), [typeof(IComponentContext), typeof(Type)])!;
    private static readonly MethodInfo GetTypeFromHandle =
        typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!;
    private static readonly MethodInfo DisposeMethod =
        typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!;
    private static readonly MethodInfo DisposeAsyncMethod =
        typeof(IAsyncDisposable).GetMethod(nameof(IAsyncDisposable.DisposeAsync))!;

    /// <summary>
    /// Returns an implementation of <typeparamref name="TWrapper"/> backed by <paramref name="scope"/>.
    /// </summary>
    public static TWrapper Create<TWrapper>(ILifetimeScope scope) where TWrapper : class =>
        (TWrapper)Factories.GetOrAdd(typeof(TWrapper), Emit)(scope);

    private static bool IsGetter(MethodInfo method) =>
        method.IsSpecialName && method.Name.StartsWith("get_", StringComparison.Ordinal) && method.GetParameters().Length == 0;

    private static bool IsDispose(MethodInfo method) =>
        method.Name == nameof(IDisposable.Dispose) && method.ReturnType == typeof(void) && method.GetParameters().Length == 0;

    private static bool IsDisposeAsync(MethodInfo method) =>
        method.Name == nameof(IAsyncDisposable.DisposeAsync) && method.ReturnType == typeof(ValueTask) && method.GetParameters().Length == 0;

    // A method inherited from a base interface (other than a getter or IDisposable/IAsyncDisposable) is
    // forwarded to that interface resolved from the scope; a method declared on the wrapper has no target.
    private static bool IsForwarded(Type iface, MethodInfo method) =>
        method.DeclaringType != iface && !IsGetter(method) && !IsDispose(method) && !IsDisposeAsync(method);

    private static Func<ILifetimeScope, object> Emit(Type iface)
    {
        if (!typeof(IDisposable).IsAssignableFrom(iface) && !typeof(IAsyncDisposable).IsAssignableFrom(iface))
            throw new ArgumentException($"'{iface}' must implement IDisposable or IAsyncDisposable so its backing scope is released.", nameof(iface));

        MethodInfo[] methods = [.. new[] { iface }.Concat(iface.GetInterfaces()).SelectMany(i => i.GetMethods())];

        // Validate up front so an unsupported member fails when the wrapper is built, not when it is called.
        foreach (MethodInfo method in methods)
            if (!IsGetter(method) && !IsDispose(method) && !IsDisposeAsync(method) && !IsForwarded(iface, method))
                throw new ArgumentException(
                    $"'{iface}' cannot be a processing-env wrapper: '{method.Name}' is declared on the wrapper itself but is not a read-only property. Only getters, methods inherited from a base interface, and IDisposable/IAsyncDisposable are supported.", nameof(iface));

        // Unique suffix so a concurrent GetOrAdd re-entry never collides on the module type name.
        TypeBuilder type = Module.DefineType(
            $"{iface.Name}_Env_{Interlocked.Increment(ref _typeCounter)}",
            TypeAttributes.Public | TypeAttributes.Sealed, typeof(object), [iface]);

        FieldBuilder scope = type.DefineField("_scope", typeof(ILifetimeScope), FieldAttributes.Private | FieldAttributes.InitOnly);

        // A backing field per getter (the component) and per forwarded interface (the delegate target),
        // all resolved from the scope in the constructor.
        Dictionary<MethodInfo, FieldBuilder> getters = [];
        foreach (MethodInfo getter in methods.Where(IsGetter))
            getters[getter] = type.DefineField($"_{getter.Name}", getter.ReturnType, FieldAttributes.Private | FieldAttributes.InitOnly);

        Dictionary<Type, FieldBuilder> forwardTargets = [];
        foreach (MethodInfo method in methods.Where(m => IsForwarded(iface, m)))
            if (!forwardTargets.ContainsKey(method.DeclaringType!))
                forwardTargets[method.DeclaringType!] = type.DefineField($"_forward{forwardTargets.Count}", method.DeclaringType!, FieldAttributes.Private | FieldAttributes.InitOnly);

        EmitConstructor(type, scope, [.. getters.Values, .. forwardTargets.Values]);

        foreach (MethodInfo method in methods)
            EmitMethod(type, scope, getters, forwardTargets, method);

        Type generated = type.CreateType()!;
        ConstructorInfo ctor = generated.GetConstructor([typeof(ILifetimeScope)])!;
        ParameterExpression scopeArg = Expression.Parameter(typeof(ILifetimeScope), "scope");
        return Expression.Lambda<Func<ILifetimeScope, object>>(Expression.New(ctor, scopeArg), scopeArg).Compile();
    }

    // .ctor(ILifetimeScope scope) { _scope = scope; _X = (TX)scope.Resolve(typeof(TX)); ... }
    private static void EmitConstructor(TypeBuilder type, FieldBuilder scope, IEnumerable<FieldBuilder> resolvedFields)
    {
        ILGenerator il = type
            .DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(ILifetimeScope)])
            .GetILGenerator();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, scope);

        foreach (FieldBuilder field in resolvedFields)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);                              // scope (IComponentContext)
            il.Emit(OpCodes.Ldtoken, field.FieldType);            // typeof(TX)
            il.Emit(OpCodes.Call, GetTypeFromHandle);
            il.Emit(OpCodes.Call, ResolveMethod);                 // object
            il.Emit(field.FieldType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, field.FieldType);
            il.Emit(OpCodes.Stfld, field);
        }

        il.Emit(OpCodes.Ret);
    }

    private static void EmitMethod(TypeBuilder type, FieldBuilder scope, Dictionary<MethodInfo, FieldBuilder> getters, Dictionary<Type, FieldBuilder> forwardTargets, MethodInfo method)
    {
        MethodBuilder impl = type.DefineMethod(method.Name,
            MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
            method.ReturnType, [.. method.GetParameters().Select(p => p.ParameterType)]);
        ILGenerator il = impl.GetILGenerator();

        if (IsDispose(method))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, scope);
            il.Emit(OpCodes.Callvirt, DisposeMethod);
            il.Emit(OpCodes.Ret);
        }
        else if (IsDisposeAsync(method))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, scope);
            il.Emit(OpCodes.Callvirt, DisposeAsyncMethod);
            il.Emit(OpCodes.Ret);
        }
        else if (IsGetter(method))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, getters[method]);
            il.Emit(OpCodes.Ret);
        }
        else
        {
            // Forward to the component resolved for the method's declaring interface.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, forwardTargets[method.DeclaringType!]);
            for (int i = 0; i < method.GetParameters().Length; i++)
                il.Emit(OpCodes.Ldarg_S, (byte)(i + 1));
            il.Emit(OpCodes.Callvirt, method);
            il.Emit(OpCodes.Ret);
        }

        type.DefineMethodOverride(impl, method);
    }
}
