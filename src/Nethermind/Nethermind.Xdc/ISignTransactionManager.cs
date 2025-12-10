// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Spec;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal interface ISignTransactionManager
{
    Task CreateTransactionSign(XdcBlockHeader header, IXdcReleaseSpec spec);
}