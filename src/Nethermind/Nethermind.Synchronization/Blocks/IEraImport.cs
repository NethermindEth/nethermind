// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Synchronization;
public interface IEraImport
{
    Task Import(string src, CancellationToken cancellation);
}