// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace JitAsm.Test;

[TestFixture]
public class StaticCtorDetectorTests
{
    [Test]
    public void DetectStaticCtors_WithDirectCctorCall_DetectsType()
    {
        const string input = """
            ; Assembly listing for method Type:Method():int (FullOpts)
            G_M000_IG01:
                   call     Namespace.SomeType:.cctor()
                   mov      eax, 42
                   ret
            ; Total bytes of code 20
            """;

        var result = StaticCtorDetector.DetectStaticCtors(input);

        result.Should().Contain("Namespace.SomeType");
    }

    [Test]
    public void DetectStaticCtors_WithMultipleCctorCalls_DetectsAllTypes()
    {
        const string input = """
            ; Assembly listing for method Type:Method():int (FullOpts)
            G_M000_IG01:
                   call     Namespace.TypeA:.cctor()
                   call     Namespace.TypeB:.cctor()
                   call     Another.TypeC:.cctor()
                   ret
            ; Total bytes of code 30
            """;

        var result = StaticCtorDetector.DetectStaticCtors(input);

        result.Should().HaveCount(3);
        result.Should().Contain("Namespace.TypeA");
        result.Should().Contain("Namespace.TypeB");
        result.Should().Contain("Another.TypeC");
    }

    [Test]
    public void DetectStaticCtors_WithNoCctorCalls_ReturnsEmpty()
    {
        const string input = """
            ; Assembly listing for method Type:Method():int (FullOpts)
            G_M000_IG01:
                   mov      eax, 42
                   ret
            ; Total bytes of code 10
            """;

        var result = StaticCtorDetector.DetectStaticCtors(input);

        result.Should().BeEmpty();
    }

    [Test]
    public void DetectStaticCtors_WithEmptyInput_ReturnsEmpty()
    {
        var result = StaticCtorDetector.DetectStaticCtors("");

        result.Should().BeEmpty();
    }

    [Test]
    public void DetectStaticCtors_WithNestedType_DetectsCorrectly()
    {
        const string input = """
            ; Assembly listing for method Type:Method():int (FullOpts)
            G_M000_IG01:
                   call     Namespace.OuterType+NestedType:.cctor()
                   ret
            ; Total bytes of code 15
            """;

        var result = StaticCtorDetector.DetectStaticCtors(input);

        result.Should().Contain("Namespace.OuterType+NestedType");
    }

    [Test]
    public void DetectStaticCtors_WithGenericType_DetectsCorrectly()
    {
        const string input = """
            ; Assembly listing for method Type:Method():int (FullOpts)
            G_M000_IG01:
                   call     Namespace.GenericType`1:.cctor()
                   ret
            ; Total bytes of code 15
            """;

        var result = StaticCtorDetector.DetectStaticCtors(input);

        result.Should().HaveCount(1);
        result[0].Should().StartWith("Namespace.GenericType");
    }

    [Test]
    public void DetectStaticCtors_WithDuplicateCalls_ReturnsUnique()
    {
        const string input = """
            ; Assembly listing for method Type:Method():int (FullOpts)
            G_M000_IG01:
                   call     Namespace.SomeType:.cctor()
                   nop
                   call     Namespace.SomeType:.cctor()
                   ret
            ; Total bytes of code 25
            """;

        var result = StaticCtorDetector.DetectStaticCtors(input);

        result.Should().HaveCount(1);
        result.Should().Contain("Namespace.SomeType");
    }

    [Test]
    public void DetectStaticCtors_WithStaticHelperCall_DetectsNearbyType()
    {
        const string input = """
            ; Assembly listing for method Type:Method():int (FullOpts)
            ; Namespace.SomeType
            G_M000_IG01:
                   call     CORINFO_HELP_GETSHARED_NONGCSTATIC_BASE
                   mov      eax, 42
                   ret
            ; Total bytes of code 20
            """;

        var result = StaticCtorDetector.DetectStaticCtors(input);

        result.Should().Contain("Namespace.SomeType");
    }

    [Test]
    public void DetectStaticCtors_WithGcStaticHelper_DetectsNearbyType()
    {
        const string input = """
            ; Assembly listing for method Type:Method():int (FullOpts)
            ; Namespace.AnotherType
            G_M000_IG01:
                   call     CORINFO_HELP_GETSHARED_GCSTATIC_BASE
                   ret
            ; Total bytes of code 15
            """;

        var result = StaticCtorDetector.DetectStaticCtors(input);

        result.Should().Contain("Namespace.AnotherType");
    }

    [Test]
    public void DetectStaticCtors_WithClassInitHelper_DetectsNearbyType()
    {
        const string input = """
            ; Assembly listing for method Type:Method():int (FullOpts)
            ; Namespace.DynamicType
            G_M000_IG01:
                   call     CORINFO_HELP_CLASSINIT_SHARED_DYNAMICCLASS
                   ret
            ; Total bytes of code 15
            """;

        var result = StaticCtorDetector.DetectStaticCtors(input);

        result.Should().Contain("Namespace.DynamicType");
    }

    [Test]
    public void DetectStaticCtors_FiltersOutRegisterNames()
    {
        const string input = """
            ; Assembly listing for method Type:Method():int (FullOpts)
            ; rax
            G_M000_IG01:
                   call     CORINFO_HELP_GETSHARED_NONGCSTATIC_BASE
                   ret
            ; Total bytes of code 15
            """;

        var result = StaticCtorDetector.DetectStaticCtors(input);

        result.Should().NotContain("rax");
    }

    [Test]
    public void DetectStaticCtors_WithStaticFieldAccess_DetectsType()
    {
        const string input = """
            ; Assembly listing for method Type:Method():int (FullOpts)
            G_M000_IG01:
                   cmp      dword ptr [Namespace.SomeType:initialized], 0
                   call     CORINFO_HELP_GETSHARED_NONGCSTATIC_BASE
                   ret
            ; Total bytes of code 20
            """;

        var result = StaticCtorDetector.DetectStaticCtors(input);

        result.Should().Contain("Namespace.SomeType");
    }
}
