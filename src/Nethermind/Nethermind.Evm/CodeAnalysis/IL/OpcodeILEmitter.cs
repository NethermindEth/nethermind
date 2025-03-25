// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Config;
using Nethermind.Int256;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.WordEmit;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using static Nethermind.Evm.CodeAnalysis.IL.StackEmit;
using Nethermind.State;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Sigil.NonGeneric;
using Nethermind.Core.Crypto;
using Org.BouncyCastle.Ocsp;
using static Org.BouncyCastle.Math.EC.ECCurve;
using Nethermind.Evm.CodeAnalysis.IL.CompilerModes;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal delegate void OpcodeILEmitterDelegate<T>(
    CodeInfo codeInfo, 
    IVMConfig ilCompilerConfig,
    ContractCompilerMetadata contractMetadata,
    SubSegmentMetadata currentSubSegment,
    int programCounter, Instruction op, OpcodeMetadata opcodeMetadata,
    Sigil.Emit<T> method, Locals<T> localVariables, EnvLoader<T> envStateLoader,
    Dictionary<EvmExceptionType, Sigil.Label> exceptions, (Label returnLabel, Label jumpTable, Label exitLabel) returnLabel);
