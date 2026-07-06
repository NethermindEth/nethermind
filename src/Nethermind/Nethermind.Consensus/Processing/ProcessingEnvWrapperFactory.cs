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
using Autofac;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Generates and caches, once per wrapper interface, a concrete class that surfaces an Autofac
/// <see cref="ILifetimeScope"/> as that interface: the constructor resolves every read-only property
/// from the scope into a backing field, each getter returns that field, and
/// <see cref="IDisposable.Dispose"/> disposes the scope.
/// </summary>
/// <remarks>
/// The generated type lives in a dynamic assembly, so a wrapper interface must be <b>public</b>.
/// Components are resolved eagerly at construction (like the constructor-injected <c>record</c> bundles
/// this replaces), so any missing registration surfaces when the wrapper is built.
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

    /// <summary>
    /// Returns an implementation of <typeparamref name="TWrapper"/> backed by <paramref name="scope"/>.
    /// </summary>
    public static TWrapper Create<TWrapper>(ILifetimeScope scope) where TWrapper : class =>
        (TWrapper)Factories.GetOrAdd(typeof(TWrapper), Emit)(scope);

    private static bool IsGetter(MethodInfo method) =>
        method.IsSpecialName && method.Name.StartsWith("get_", StringComparison.Ordinal) && method.GetParameters().Length == 0;

    private static bool IsDispose(MethodInfo method) =>
        method.Name == nameof(IDisposable.Dispose) && method.ReturnType == typeof(void) && method.GetParameters().Length == 0;

    private static Func<ILifetimeScope, object> Emit(Type iface)
    {
        // Unique suffix so a concurrent GetOrAdd re-entry never collides on the module type name.
        TypeBuilder type = Module.DefineType(
            $"{iface.Name}_Env_{Interlocked.Increment(ref _typeCounter)}",
            TypeAttributes.Public | TypeAttributes.Sealed, typeof(object), [iface]);

        FieldBuilder scope = type.DefineField("_scope", typeof(ILifetimeScope), FieldAttributes.Private | FieldAttributes.InitOnly);

        MethodInfo[] methods = [.. new[] { iface }.Concat(iface.GetInterfaces()).SelectMany(i => i.GetMethods())];

        // A backing field per getter, holding the component resolved in the constructor.
        Dictionary<MethodInfo, FieldBuilder> fields = [];
        foreach (MethodInfo getter in methods.Where(IsGetter))
            fields[getter] = type.DefineField(
                $"_{getter.Name}", getter.ReturnType, FieldAttributes.Private | FieldAttributes.InitOnly);

        EmitConstructor(type, scope, fields);

        foreach (MethodInfo method in methods)
            EmitMethod(type, scope, fields, method);

        Type generated = type.CreateType()!;
        ConstructorInfo ctor = generated.GetConstructor([typeof(ILifetimeScope)])!;
        ParameterExpression scopeArg = Expression.Parameter(typeof(ILifetimeScope), "scope");
        return Expression.Lambda<Func<ILifetimeScope, object>>(Expression.New(ctor, scopeArg), scopeArg).Compile();
    }

    // .ctor(ILifetimeScope scope) { _scope = scope; _X = (TX)scope.Resolve(typeof(TX)); ... }
    private static void EmitConstructor(TypeBuilder type, FieldBuilder scope, Dictionary<MethodInfo, FieldBuilder> fields)
    {
        ILGenerator il = type
            .DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(ILifetimeScope)])
            .GetILGenerator();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, scope);

        foreach (FieldBuilder field in fields.Values)
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

    private static void EmitMethod(TypeBuilder type, FieldBuilder scope, Dictionary<MethodInfo, FieldBuilder> fields, MethodInfo method)
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
        else if (IsGetter(method))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, fields[method]);
            il.Emit(OpCodes.Ret);
        }
        else
        {
            // Keep the type verifiable but reject non-property members at call time.
            il.Emit(OpCodes.Ldstr, $"'{method.Name}' is not supported on a processing-env wrapper; only read-only properties and IDisposable.Dispose are.");
            il.Emit(OpCodes.Newobj, typeof(NotSupportedException).GetConstructor([typeof(string)])!);
            il.Emit(OpCodes.Throw);
        }

        type.DefineMethodOverride(impl, method);
    }
}
