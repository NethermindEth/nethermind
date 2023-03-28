// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.EOF;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public class EofCodeInfo : ICodeInfo
{
    private readonly CodeInfo _codeInfo;
    private readonly EofHeader _header;
    private ICodeInfoAnalyzer? _analyzer;
    public byte[] MachineCode => _codeInfo.MachineCode;
    public IPrecompile? Precompile => _codeInfo.Precompile;
    public byte Version => _header.Version;
    public ReadOnlyMemory<byte> TypeSection { get; }
    public ReadOnlyMemory<byte> CodeSection { get; }
    public ReadOnlyMemory<byte> DataSection { get; }
    public ReadOnlyMemory<byte> ContainerSection { get; }
    public (int, int) SectionOffset(int sectionId) => (_header.CodeSections[sectionId].Start - _header.TypeSection.EndOffset, _header.CodeSections[sectionId].Size);

    public bool ValidateJump(int destination, bool isSubroutine)
    {
        _analyzer ??= CodeInfo.CreateAnalyzer(CodeSection);
        return _analyzer.ValidateJump(destination, isSubroutine);
    }

    public EofCodeInfo(CodeInfo codeInfo, in EofHeader header)
    {
        _codeInfo = codeInfo;
        _header = header;
        ReadOnlyMemory<byte> memory = MachineCode.AsMemory();
        TypeSection = memory.Slice(_header.TypeSection.Start, _header.TypeSection.Size);
        CodeSection = memory.Slice(_header.CodeSections[0].Start, _header.CodeSectionsSize);
        DataSection = memory.Slice(_header.DataSection.Start, _header.DataSection.Size);
        ContainerSection = memory.Slice(_header.ContainerSection[0].Start, _header.ExtraContainersSize);
    }
}
