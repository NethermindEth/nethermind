// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MathNet.Numerics.LinearAlgebra.Factorization;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Era1;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Era1;
using Nethermind.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules;
public class AdminEraService : IAdminEraService
{
    private readonly ILogger _logger;
    private readonly IBlockTree _blockTree;
    private readonly IEraExporter _eraExporter;
    private readonly IProcessExitToken _processExit;
    private int _canEnterExport = 1;
    private int _canEnterVerification = 1;

    public AdminEraService(IBlockTree blockTree, IEraExporter eraExporter, IProcessExitToken processExit, ILogManager logManager)
    {
        _blockTree = blockTree;
        _eraExporter = eraExporter;
        this._processExit = processExit;
        _logger = logManager.GetClassLogger();
    }

    public ResultWrapper<string> ExportHistory(string destination, int epochFrom, int epochTo)
    {
        //TODO sanitize destination path
        if (epochFrom < 0 || epochTo < 0)
            return ResultWrapper<string>.Fail("Epoch number cannot be negative.");
        if (epochTo < epochFrom)
            return ResultWrapper<string>.Fail($"Invalid range {epochFrom}-{epochTo}.");

        Block? latestHead = _blockTree.Head;
        if (latestHead == null)
            return ResultWrapper<string>.Fail("Node is currently unable to export.");

        int from = epochFrom * EraWriter.MaxEra1Size;
        int to = epochTo * EraWriter.MaxEra1Size + EraWriter.MaxEra1Size - 1;
        long remainingInEpoch = EraWriter.MaxEra1Size - latestHead.Number % EraWriter.MaxEra1Size;
        long mostRecentFinishedEpoch = (latestHead.Number == 0 ? 0 : latestHead.Number / EraWriter.MaxEra1Size) - (remainingInEpoch == 0 ? 0 : 1);
        if (mostRecentFinishedEpoch < 0)
            return ResultWrapper<string>.Fail($"No epochs ready for export.");
        if (mostRecentFinishedEpoch < epochFrom || mostRecentFinishedEpoch < epochTo)
            return ResultWrapper<string>.Fail($"Cannot export beyond epoch {mostRecentFinishedEpoch}.");

        Block? earliest = _blockTree.FindBlock(from, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);

        if (earliest == null)
            return ResultWrapper<string>.Fail($"Cannot export epoch {epochFrom}.");

        if (Interlocked.Exchange(ref _canEnterExport, 0) == 1)
        {
            StartExportTask(destination, from, to).ContinueWith((_) =>
            {
                Interlocked.Exchange(ref _canEnterExport, 1);
            });
            //TODO better message?
            return ResultWrapper<string>.Success("Started export task");
        }
        else
        {
            return ResultWrapper<string>.Fail("An export job is already running.");
        }
    }

    private async Task StartExportTask(string destination, long from, long to)
    {
        try
        {
            _eraExporter.ExportProgress += LogExportProgress;

            if (_logger.IsInfo) _logger.Info($"Starting history export from {from} to {to}");
            await _eraExporter.Export(
                destination,
                from,
                to,
                EraWriter.MaxEra1Size,
                _processExit.Token);
            if (_logger.IsInfo) _logger.Info($"Finished history export from {from} to {to}");
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Error($"A running export job was cancelled. Exported archives in '{destination}' might be in a corrupted state.");
        }
        catch (EraException e)
        {
            _logger.Error("Import error", e);
        }
        catch (Exception e)
        {
            _logger.Error("Export error", e);
        }
        finally
        {
            _eraExporter.ExportProgress -= LogExportProgress;
        }
    }

    public ResultWrapper<string> VerifyHistory(string eraSource, string accumulatorFile)
    {
        if (Interlocked.Exchange(ref _canEnterVerification, 0) == 1)
        {
            StartVerificationTask(eraSource, accumulatorFile).ContinueWith((t) =>
            {
                Interlocked.Exchange(ref _canEnterVerification, 1);
            });

            return ResultWrapper<string>.Success("Started history verification");
        }
        else
        {
            return ResultWrapper<string>.Fail("A verification job is currently running.");
        }
    }

    private async Task StartVerificationTask(string eraSource, string accumulatorFile)
    {
        try
        {
            _eraExporter.VerificationProgress += LogVerificationProgress;
            await _eraExporter.VerifyEraFiles(eraSource, accumulatorFile, _processExit.Token);
        }
        catch (EraVerificationException e)
        {
            _logger.Error(e.Message);
        }
        finally
        {
            _eraExporter.VerificationProgress -= LogVerificationProgress;
        }
    }

    private void LogExportProgress(object sender, ExportProgressArgs args)
    {
        if (_logger.IsInfo)
            _logger.Info($"Export progress: {args.BlockProcessedSinceLast,10}/{args.TotalBlocks} blocks  |  elapsed {args.Elapsed:hh\\:mm\\:ss}  |  {args.BlockProcessedSinceLast / args.ElapsedSinceLast.TotalSeconds,10:0.##} Blk/s  |  {args.TxProcessedSinceLast / args.ElapsedSinceLast.TotalSeconds,10:0.##} tx/s");
    }

    private void LogVerificationProgress(object sender, VerificationProgressArgs args)
    {
        if (_logger.IsInfo)
            _logger.Info($"Verification progress: {args.Processed,10}/{args.TotalToProcess} archives  |  elapsed {args.Elapsed:hh\\:mm\\:ss}  |  {args.Processed / args.Elapsed.TotalSeconds,10:0.##} archives/s");
    }
}
