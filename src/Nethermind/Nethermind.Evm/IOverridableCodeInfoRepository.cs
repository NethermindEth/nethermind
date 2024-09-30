// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public interface IOverridableCodeInfoRepository : ICodeInfoRepository
{
    public void ResetOverrides();
}
