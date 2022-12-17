// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Reflection;
using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NuGet.Frameworks;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis
{
    [TestFixture]
    public class CodeInfoTests
    {
        private const string AnalyzerField = "_analyzer";

        [TestCase(-1, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        public void Validates_when_only_jump_dest_present(int destination, bool isValid)
        {
            var spec = new OverridableReleaseSpec(Berlin.Instance)
            {
                IsEip2315Enabled = true
            };

            byte[] code =
            {
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code, spec);

            codeInfo.ValidateJump(destination, false, spec).Should().Be(isValid);
        }

        [TestCase(-1, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        public void Validates_when_only_begin_sub_present(int destination, bool isValid)
        {
            var spec = new OverridableReleaseSpec(Berlin.Instance)
            {
                IsEip2315Enabled = true
            };

            byte[] code =
            {
                (byte)Instruction.BEGINSUB
            };

            CodeInfo codeInfo = new(code, spec);


            codeInfo.ValidateJump(destination, true, spec).Should().Be(isValid);
        }

        [Test]
        public void Validates_when_push_with_data_like_jump_dest()
        {
            var spec = new OverridableReleaseSpec(Berlin.Instance)
            {
                IsEip2315Enabled = true
            };

            byte[] code =
            {
                (byte)Instruction.PUSH1,
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code, spec);

            codeInfo.ValidateJump(1, true, spec).Should().BeFalse();
            codeInfo.ValidateJump(1, false, spec).Should().BeFalse();
        }

        [Test]
        public void Validates_when_push_with_data_like_begin_sub()
        {
            var spec = new OverridableReleaseSpec(Berlin.Instance)
            {
                IsEip2315Enabled = true
            };

            byte[] code =
            {
                (byte)Instruction.PUSH1,
                (byte)Instruction.BEGINSUB
            };

            CodeInfo codeInfo = new(code, spec);

            codeInfo.ValidateJump(1, true, spec).Should().BeFalse();
            codeInfo.ValidateJump(1, false, spec).Should().BeFalse();
        }

        [Test]
        public void Validate_CodeBitmap_With_Push10()
        {
            IReleaseSpec spec = GrayGlacier.Instance;
            byte[] code =
            {
                (byte)Instruction.PUSH10,
                1,2,3,4,5,6,7,8,9,10,
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code, spec);

            codeInfo.ValidateJump(11, false, spec).Should().BeTrue();
        }

        [Test]
        public void Validate_CodeBitmap_With_Push30()
        {
            IReleaseSpec spec = GrayGlacier.Instance;
            byte[] code =
            {
                (byte)Instruction.PUSH30,
                1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code, spec);

            codeInfo.ValidateJump(31, false, spec).Should().BeTrue();
        }

        [Test]
        public void Small_Jumpdest_Use_CodeDataAnalyzer()
        {
            IReleaseSpec spec = GrayGlacier.Instance;
            byte[] code =
            {
                0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b
            };

            CodeInfo codeInfo = new(code, spec);

            codeInfo.ValidateJump(10, false, spec).Should().BeTrue();

            FieldInfo field = typeof(CodeInfo).GetField(AnalyzerField, BindingFlags.Instance | BindingFlags.NonPublic);
            var calc = field.GetValue(codeInfo);

            Assert.IsInstanceOf<CodeDataAnalyzer>(calc);
        }

        [Test]
        public void Small_Push1_Use_CodeDataAnalyzer()
        {
            IReleaseSpec spec = GrayGlacier.Instance;
            byte[] code =
            {
                0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,
            };

            CodeInfo codeInfo = new(code, spec);

            codeInfo.ValidateJump(10, false, spec).Should().BeFalse();

            FieldInfo field = typeof(CodeInfo).GetField(AnalyzerField, BindingFlags.Instance | BindingFlags.NonPublic);
            var calc = field.GetValue(codeInfo);

            Assert.IsInstanceOf<CodeDataAnalyzer>(calc);
        }

        [Test]
        public void Jumpdest_Over10k_Use_JumpdestAnalyzer()
        {
            IReleaseSpec spec = GrayGlacier.Instance;
            var code = Enumerable.Repeat((byte)0x5b, 10_001).ToArray();

            CodeInfo codeInfo = new(code, spec);

            codeInfo.ValidateJump(10, false, spec).Should().BeTrue();

            FieldInfo field = typeof(CodeInfo).GetField(AnalyzerField, BindingFlags.Instance | BindingFlags.NonPublic);
            var calc = field.GetValue(codeInfo);

            Assert.IsInstanceOf<CodeDataAnalyzer>(calc);
        }

        [Test]
        public void Push1_Over10k_Use_JumpdestAnalyzer()
        {
            IReleaseSpec spec = GrayGlacier.Instance;
            var code = Enumerable.Repeat((byte)0x60, 10_001).ToArray();

            CodeInfo codeInfo = new(code, spec);

            codeInfo.ValidateJump(10, false, spec).Should().BeFalse();

            FieldInfo field = typeof(CodeInfo).GetField(AnalyzerField, BindingFlags.Instance | BindingFlags.NonPublic);
            var calc = field.GetValue(codeInfo);

            Assert.IsInstanceOf<JumpdestAnalyzer>(calc);
        }

        [Test]
        public void Push1Jumpdest_Over10k_Use_JumpdestAnalyzer()
        {
            IReleaseSpec spec = GrayGlacier.Instance;
            byte[] code = new byte[10_001];
            for (int i = 0; i < code.Length; i++)
            {
                code[i] = i % 2 == 0 ? (byte)0x60 : (byte)0x5b;
            }

            ICodeInfoAnalyzer calc = null;
            int iterations = 1;
            while (iterations <= 10)
            {
                CodeInfo codeInfo = new(code, spec);

                codeInfo.ValidateJump(10, false, spec).Should().BeFalse();
                codeInfo.ValidateJump(11, false, spec).Should().BeFalse(); // 0x5b but not JUMPDEST but data

                FieldInfo field = typeof(CodeInfo).GetField(AnalyzerField, BindingFlags.Instance | BindingFlags.NonPublic);
                calc = (ICodeInfoAnalyzer)field.GetValue(codeInfo);

                if (calc is JumpdestAnalyzer)
                {
                    break;
                }

                iterations++;
            }

            Assert.IsInstanceOf<JumpdestAnalyzer>(calc);
        }
    }
}
