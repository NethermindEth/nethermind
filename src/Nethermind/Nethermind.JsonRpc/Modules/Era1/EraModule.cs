// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Era1;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Era1;
internal class EraModule
{
    private IEraService _eraService;
    private Task _exportTask = Task.CompletedTask;
    private SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1);

    public EraModule(IEraService eraService)
    {
        _eraService = eraService;
    }
    public async Task<ResultWrapper<string>> start_export_history(string destination, int blockStart, int count)
    {
        await _semaphoreSlim.WaitAsync();
        try
        {
            if (!_exportTask.IsCompleted)
            {
                return ResultWrapper<string>.Fail("An export job is already running");
            }
            _exportTask = _eraService.Export(destination, "", blockStart, count);
            return ResultWrapper<string>.Success("");
        }
        finally
        {
            _semaphoreSlim.Release();                   
        }
    }

    //public async Task<ResultWrapper<string>> import_history(string enode, bool removeFromStaticNodes = false)
    //{

    //    return ResultWrapper<string>.Success(enode);
    //}
}
