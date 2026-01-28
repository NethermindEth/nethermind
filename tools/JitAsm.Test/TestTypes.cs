// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace JitAsm.Test.TestTypes;

public class SimpleClass
{
    public int SimpleMethod() => 42;

    public static int StaticMethod() => 100;

    public int OverloadedMethod() => 1;
    public int OverloadedMethod(int x) => x;
    public int OverloadedMethod(int x, int y) => x + y;

    private int PrivateMethod() => 0;

    public int MethodWithParams(int a, string b, double c) => a;
}

public class GenericClass<T>
{
    public T GenericMethod(T value) => value;

    public TResult GenericMethodWithResult<TResult>(T value) where TResult : new() => new();
}

public static class StaticClass
{
    public static int StaticOnlyMethod() => 1;

    public static T GenericStaticMethod<T>(T value) => value;
}

public class ClassWithNestedTypes
{
    public class NestedClass
    {
        public int NestedMethod() => 1;
    }

    public struct NestedStruct
    {
        public int NestedStructMethod() => 2;
    }
}

public interface ITestFlag
{
    static virtual bool IsActive => false;
}

public struct OnTestFlag : ITestFlag
{
    public static bool IsActive => true;
}

public struct OffTestFlag : ITestFlag
{
    public static bool IsActive => false;
}

public class ClassWithGenericConstraints
{
    public void ConstrainedMethod<T>() where T : struct, ITestFlag { }
}

public class GenericContainingClass<TPolicy> where TPolicy : struct
{
    public int NonGenericMethod() => 42;

    public TResult GenericMethod<TResult>() where TResult : struct, ITestFlag => default;

    private int PrivateGenericMethod<TFlag>() where TFlag : struct, ITestFlag => TFlag.IsActive ? 1 : 0;
}
