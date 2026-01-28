// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace JitAsm.Test;

[TestFixture]
public class DisassemblyParserTests
{
    [Test]
    public void Parse_WithValidDisassembly_ExtractsMethodOutput()
    {
        const string input = """
            ; Assembly listing for method Namespace.Type:Method():int (FullOpts)
            ; Emitting BLENDED_CODE for generic X64 + VEX + EVEX on Windows
            ; FullOpts code
            ; optimized code

            G_M000_IG01:                ;; offset=0x0000
                   sub      rsp, 40

            G_M000_IG02:                ;; offset=0x0004
                   mov      eax, 42

            G_M000_IG03:                ;; offset=0x0009
                   add      rsp, 40
                   ret

            ; Total bytes of code 14
            """;

        var result = DisassemblyParser.Parse(input);

        result.Should().Contain("Assembly listing for method Namespace.Type:Method()");
        result.Should().Contain("mov      eax, 42");
        result.Should().Contain("Total bytes of code 14");
    }

    [Test]
    public void Parse_WithMultipleMethods_ExtractsAllMethods()
    {
        const string input = """
            ; Assembly listing for method Type:Method1():int (FullOpts)
            ; FullOpts code

            G_M000_IG01:
                   mov      eax, 1
                   ret

            ; Total bytes of code 5

            ; Assembly listing for method Type:Method2():int (FullOpts)
            ; FullOpts code

            G_M000_IG01:
                   mov      eax, 2
                   ret

            ; Total bytes of code 5
            """;

        var result = DisassemblyParser.Parse(input);

        result.Should().Contain("Type:Method1()");
        result.Should().Contain("mov      eax, 1");
        result.Should().Contain("Type:Method2()");
        result.Should().Contain("mov      eax, 2");
    }

    [Test]
    public void Parse_WithEmptyInput_ReturnsEmptyString()
    {
        var result = DisassemblyParser.Parse("");

        result.Should().BeEmpty();
    }

    [Test]
    public void Parse_WithWhitespaceOnly_ReturnsEmptyString()
    {
        var result = DisassemblyParser.Parse("   \n\t\n   ");

        result.Should().BeEmpty();
    }

    [Test]
    public void Parse_WithNoMethodHeaders_ReturnsEmptyOrRaw()
    {
        const string input = """
            Some random text
            that doesn't contain method headers
            """;

        var result = DisassemblyParser.Parse(input);

        result.Should().BeEmpty();
    }

    [Test]
    public void Parse_WithAssemblyLikeContentButNoHeaders_ReturnsRaw()
    {
        const string input = """
            mov      eax, 42
            call     SomeMethod
            ret
            """;

        var result = DisassemblyParser.Parse(input);

        result.Should().Contain("mov");
        result.Should().Contain("call");
        result.Should().Contain("ret");
    }

    [Test]
    public void ParseMethods_WithMultipleMethods_ReturnsEnumerable()
    {
        const string input = """
            ; Assembly listing for method Type:Method1():int (FullOpts)
            ; FullOpts code
            G_M000_IG01:
                   mov      eax, 1
            ; Total bytes of code 5

            ; Assembly listing for method Type:Method2():int (FullOpts)
            ; FullOpts code
            G_M000_IG01:
                   mov      eax, 2
            ; Total bytes of code 5
            """;

        var methods = DisassemblyParser.ParseMethods(input).ToList();

        methods.Should().HaveCount(2);
        methods[0].MethodName.Should().Be("Type:Method1():int (FullOpts)");
        methods[0].Assembly.Should().Contain("mov      eax, 1");
        methods[1].MethodName.Should().Be("Type:Method2():int (FullOpts)");
        methods[1].Assembly.Should().Contain("mov      eax, 2");
    }

    [Test]
    public void ParseMethods_WithEmptyInput_ReturnsEmpty()
    {
        var methods = DisassemblyParser.ParseMethods("").ToList();

        methods.Should().BeEmpty();
    }

    [Test]
    public void Parse_WithGenericMethod_ExtractsCorrectly()
    {
        const string input = """
            ; Assembly listing for method Namespace.Type:Method[System.Int32](int):int (FullOpts)
            ; FullOpts code

            G_M000_IG01:
                   mov      eax, ecx
                   ret

            ; Total bytes of code 4
            """;

        var result = DisassemblyParser.Parse(input);

        result.Should().Contain("Type:Method[System.Int32]");
    }
}
