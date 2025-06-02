using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Evm;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using NUnit.Framework;


namespace Nethermind.ContractSearch.Plugin.Test;

[TestFixture]
public class PatternSearchTests
{
    private OpcodeIndexer _opcodeIndexer;

    [SetUp]
    public void Setup()
    {
        _opcodeIndexer = new OpcodeIndexer();
    }

    [Test]
    [TestCaseSource(nameof(SyntacticSearchTestCases))]
    public void SyntacticSearchTests(byte[] code, byte[] pattern, int expectedMatches)
    {
        ReadOnlySpan<byte> codeSpan = code;
        ReadOnlySpan<byte> patternSpan = pattern;
        var codeHash = Keccak.Compute(code.AsSpan());
        _opcodeIndexer.Index(codeHash, codeSpan);
        var results = PatternSearch.SyntacticPatternSearch(codeHash, codeSpan, patternSpan, _opcodeIndexer);
        Assert.That(results.Count, Is.EqualTo(expectedMatches));
    }


    public static IEnumerable<TestCaseData> SyntacticSearchTestCases
    {
        get
        {
            yield return new TestCaseData(
            new byte[] { 0x60, 0x00, 0x60, 0x00, 0x60, 0x00 },
            new byte[] { 0x60, 0x00 },
            3
            ).SetName("PatternSearchTest1");

            yield return new TestCaseData(
            new byte[] { 0x60, 0x00, 0x60, 0x00, 0x60, 0x00 },
            new byte[] { 0x61, 0x00 },
            0
            ).SetName("PatternSearchTest2");

            yield return new TestCaseData(
                    Prepare.EvmCode
                    .PUSHx([1])
                    .PUSHx([1])
                    .ADD()
                    .STOP()
                    .Done,
                    Prepare.EvmCode
                    .PUSHx([1])
                    .Done,
                    2
            ).SetName("PatternSearchTest3");

            yield return new TestCaseData(
                     Prepare.EvmCode
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .PUSHx([1])
                     .ADD()
                     .STOP()
                     .Done,
                     Prepare.EvmCode
                     .PUSHx([1])
                     .Done,
                     37
             ).SetName("PatternSearchTest4-long pattern");

            yield return new TestCaseData(
            new byte[] { 0x61, 0x60, 0x1 },  // PuSH2 (PUSH1 1)
            new byte[] { 0x61, 0x1 },
            0
            ).SetName("PatternSearchTest4-syntax-should-not-match-push1");

        }
    }

}
