// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using FluentAssertions;
using JitAsm.Test.TestTypes;
using NUnit.Framework;

namespace JitAsm.Test;

[TestFixture]
public class MethodResolverTests
{
    private MethodResolver _resolver = null!;
    private Assembly _testAssembly = null!;

    [SetUp]
    public void Setup()
    {
        _testAssembly = typeof(SimpleClass).Assembly;
        _resolver = new MethodResolver(_testAssembly);
    }

    [Test]
    public void ResolveMethod_WithTypeAndMethodName_FindsMethod()
    {
        var method = _resolver.ResolveMethod(
            typeof(SimpleClass).FullName,
            nameof(SimpleClass.SimpleMethod),
            null);

        method.Should().NotBeNull();
        method!.Name.Should().Be(nameof(SimpleClass.SimpleMethod));
        method.DeclaringType.Should().Be(typeof(SimpleClass));
    }

    [Test]
    public void ResolveMethod_WithoutTypeName_SearchesAllTypes()
    {
        var method = _resolver.ResolveMethod(
            null,
            nameof(SimpleClass.SimpleMethod),
            null);

        method.Should().NotBeNull();
        method!.Name.Should().Be(nameof(SimpleClass.SimpleMethod));
    }

    [Test]
    public void ResolveMethod_WithStaticMethod_FindsMethod()
    {
        var method = _resolver.ResolveMethod(
            typeof(SimpleClass).FullName,
            nameof(SimpleClass.StaticMethod),
            null);

        method.Should().NotBeNull();
        method!.IsStatic.Should().BeTrue();
    }

    [Test]
    public void ResolveMethod_WithPrivateMethod_FindsMethod()
    {
        var method = _resolver.ResolveMethod(
            typeof(SimpleClass).FullName,
            "PrivateMethod",
            null);

        method.Should().NotBeNull();
        method!.IsPrivate.Should().BeTrue();
    }

    [Test]
    public void ResolveMethod_WithNonExistentMethod_ReturnsNull()
    {
        var method = _resolver.ResolveMethod(
            typeof(SimpleClass).FullName,
            "NonExistentMethod",
            null);

        method.Should().BeNull();
    }

    [Test]
    public void ResolveMethod_WithNonExistentType_ReturnsNull()
    {
        var method = _resolver.ResolveMethod(
            "NonExistent.Type",
            "SomeMethod",
            null);

        method.Should().BeNull();
    }

    [Test]
    public void ResolveMethod_WithOverloadedMethods_ReturnsFirstMatch()
    {
        var method = _resolver.ResolveMethod(
            typeof(SimpleClass).FullName,
            nameof(SimpleClass.OverloadedMethod),
            null);

        method.Should().NotBeNull();
        method!.Name.Should().Be(nameof(SimpleClass.OverloadedMethod));
    }

    [Test]
    public void ResolveMethod_WithStaticOnlyClass_FindsMethod()
    {
        var method = _resolver.ResolveMethod(
            typeof(StaticClass).FullName,
            nameof(StaticClass.StaticOnlyMethod),
            null);

        method.Should().NotBeNull();
        method!.IsStatic.Should().BeTrue();
    }

    [Test]
    public void ResolveMethod_WithShortTypeName_FindsMethod()
    {
        var method = _resolver.ResolveMethod(
            nameof(SimpleClass),
            nameof(SimpleClass.SimpleMethod),
            null);

        method.Should().NotBeNull();
    }

    [Test]
    public void ResolveMethod_WithGenericMethodAndTypeParams_CreatesGenericMethod()
    {
        var method = _resolver.ResolveMethod(
            typeof(StaticClass).FullName,
            nameof(StaticClass.GenericStaticMethod),
            "System.Int32");

        method.Should().NotBeNull();
        method!.IsGenericMethod.Should().BeTrue();
        method.IsGenericMethodDefinition.Should().BeFalse();
        method.GetGenericArguments().Should().ContainSingle()
            .Which.Should().Be(typeof(int));
    }

    [Test]
    public void ResolveMethod_WithGenericMethodAndAlias_CreatesGenericMethod()
    {
        var method = _resolver.ResolveMethod(
            typeof(StaticClass).FullName,
            nameof(StaticClass.GenericStaticMethod),
            "int");

        method.Should().NotBeNull();
        method!.GetGenericArguments().Should().ContainSingle()
            .Which.Should().Be(typeof(int));
    }

    [Test]
    public void ResolveMethod_WithMultipleTypeParams_CreatesGenericMethod()
    {
        // Using a method that takes multiple type params would need a different test type
        // For now, just verify single type param works with various aliases
        var method = _resolver.ResolveMethod(
            typeof(StaticClass).FullName,
            nameof(StaticClass.GenericStaticMethod),
            "string");

        method.Should().NotBeNull();
        method!.GetGenericArguments().Should().ContainSingle()
            .Which.Should().Be(typeof(string));
    }

    [Test]
    public void ResolveMethod_WithConstrainedGeneric_CreatesGenericMethod()
    {
        var method = _resolver.ResolveMethod(
            typeof(ClassWithGenericConstraints).FullName,
            nameof(ClassWithGenericConstraints.ConstrainedMethod),
            typeof(OnTestFlag).FullName);

        method.Should().NotBeNull();
        method!.GetGenericArguments().Should().ContainSingle()
            .Which.Should().Be(typeof(OnTestFlag));
    }

    [Test]
    public void ResolveMethod_WithNestedType_FindsMethod()
    {
        var nestedTypeName = typeof(ClassWithNestedTypes.NestedClass).FullName;

        var method = _resolver.ResolveMethod(
            nestedTypeName,
            nameof(ClassWithNestedTypes.NestedClass.NestedMethod),
            null);

        method.Should().NotBeNull();
    }

    [Test]
    public void FindAllMethods_WithTypeName_ReturnsAllOverloads()
    {
        var methods = _resolver.FindAllMethods(
            typeof(SimpleClass).FullName,
            nameof(SimpleClass.OverloadedMethod)).ToList();

        methods.Should().HaveCount(3);
        methods.Should().OnlyContain(m => m.Name == nameof(SimpleClass.OverloadedMethod));
    }

    [Test]
    public void FindAllMethods_WithoutTypeName_SearchesAllTypes()
    {
        var methods = _resolver.FindAllMethods(
            null,
            nameof(SimpleClass.SimpleMethod)).ToList();

        methods.Should().NotBeEmpty();
    }

    [Test]
    public void FindAllMethods_WithNonExistentMethod_ReturnsEmpty()
    {
        var methods = _resolver.FindAllMethods(
            typeof(SimpleClass).FullName,
            "NonExistentMethod").ToList();

        methods.Should().BeEmpty();
    }

    [TestCase("bool", typeof(bool))]
    [TestCase("byte", typeof(byte))]
    [TestCase("sbyte", typeof(sbyte))]
    [TestCase("char", typeof(char))]
    [TestCase("short", typeof(short))]
    [TestCase("ushort", typeof(ushort))]
    [TestCase("int", typeof(int))]
    [TestCase("uint", typeof(uint))]
    [TestCase("long", typeof(long))]
    [TestCase("ulong", typeof(ulong))]
    [TestCase("float", typeof(float))]
    [TestCase("double", typeof(double))]
    [TestCase("decimal", typeof(decimal))]
    [TestCase("string", typeof(string))]
    [TestCase("object", typeof(object))]
    public void ResolveMethod_WithTypeAlias_ResolvesCorrectType(string alias, Type expectedType)
    {
        var method = _resolver.ResolveMethod(
            typeof(StaticClass).FullName,
            nameof(StaticClass.GenericStaticMethod),
            alias);

        method.Should().NotBeNull();
        method!.GetGenericArguments().Should().ContainSingle()
            .Which.Should().Be(expectedType);
    }

    [Test]
    public void ResolveMethod_WithGenericContainingType_FindsNonGenericMethod()
    {
        var method = _resolver.ResolveMethod(
            "GenericContainingClass",
            "NonGenericMethod",
            typeParams: null,
            classTypeParams: typeof(OnTestFlag).FullName);

        method.Should().NotBeNull();
        method!.Name.Should().Be("NonGenericMethod");
        method.DeclaringType!.IsGenericType.Should().BeTrue();
        method.DeclaringType.GetGenericArguments().Should().ContainSingle()
            .Which.Should().Be(typeof(OnTestFlag));
    }

    [Test]
    public void ResolveMethod_WithGenericContainingType_FindsGenericMethod()
    {
        var method = _resolver.ResolveMethod(
            "GenericContainingClass",
            "GenericMethod",
            typeParams: typeof(OnTestFlag).FullName,
            classTypeParams: typeof(OnTestFlag).FullName);

        method.Should().NotBeNull();
        method!.Name.Should().Be("GenericMethod");
        method.IsGenericMethod.Should().BeTrue();
        method.GetGenericArguments().Should().ContainSingle()
            .Which.Should().Be(typeof(OnTestFlag));
    }

    [Test]
    public void ResolveMethod_WithGenericContainingType_FindsPrivateGenericMethod()
    {
        var method = _resolver.ResolveMethod(
            "GenericContainingClass",
            "PrivateGenericMethod",
            typeParams: typeof(OnTestFlag).FullName,
            classTypeParams: typeof(OnTestFlag).FullName);

        method.Should().NotBeNull();
        method!.Name.Should().Be("PrivateGenericMethod");
        method.IsPrivate.Should().BeTrue();
        method.IsGenericMethod.Should().BeTrue();
    }
}
