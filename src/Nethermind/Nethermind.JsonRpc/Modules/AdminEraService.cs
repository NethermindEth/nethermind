// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Era1;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Era1;
using Nethermind.Facade.Eth;
using Nethermind.Logging;
using System;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules;
public class AdminEraService : IAdminEraService
{
    private readonly ILogger _logger;
    private readonly IBlockTree _blockTree;
    private readonly IEraImporter _eraImporter;
    private readonly IEraExporter _eraExporter;
    private readonly IEthSyncingInfo _ethSyncingInfo;
    private readonly IProcessExitToken _processExit;
    private readonly IFileSystem _fileSystem;
    private int _canEnterImport = 1;
    private int _canEnterExport = 1;
    private int _canEnterVerification = 1;

    public AdminEraService(
        IBlockTree blockTree,
        IEraImporter eraImporter,
        IEraExporter eraExporter,
        IEthSyncingInfo ethSyncingInfo,
        IProcessExitToken processExit,
        IFileSystem fileSystem,
        ILogManager logManager)
    {
        _blockTree = blockTree;
        this._eraImporter = eraImporter;
        _eraExporter = eraExporter;
        this._ethSyncingInfo = ethSyncingInfo;
        this._processExit = processExit;
        this._fileSystem = fileSystem;
        _logger = logManager.GetClassLogger();
    }

    public ResultWrapper<string> ExportHistory(string destination, int epochFrom, int epochTo)
    {
        //TODO sanitize destination path
        if (epochFrom < 0 || epochTo < 0)
            return ResultWrapper<string>.Fail("Epoch number cannot be negative.");
        if (epochTo < epochFrom)
            return ResultWrapper<string>.Fail($"Invalid range {epochFrom}-{epochTo}.");
        if (!_fileSystem.Directory.Exists(destination))
            //TODO consider if this is too sensitive information
            return ResultWrapper<string>.Fail($"The directory does not exists.");
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
    public ResultWrapper<string> ImportHistory(string source, string accumulatorFile, int epochFrom, int epochTo)
    {
        //TODO sanitize destination path
        if (epochFrom < 0 || epochTo < 0)
            return ResultWrapper<string>.Fail("Epoch number cannot be negative.");
        if (epochTo < epochFrom)
            return ResultWrapper<string>.Fail($"Invalid range {epochFrom}-{epochTo}.");
        if (!_fileSystem.Directory.Exists(source))
            //TODO consider if this is too sensitive information
            return ResultWrapper<string>.Fail($"The directory does not exists.");
        if(!_fileSystem.File.Exists(accumulatorFile))
            return ResultWrapper<string>.Fail($"The file does not exists.");

        //TODO check if node is syncing
        if (_ethSyncingInfo.IsSyncing())
            return ResultWrapper<string>.Fail($"Import cannot be started while node is syncing.");

        if (Interlocked.Exchange(ref _canEnterImport, 0) == 1)
        {
            StartImportTask(source, accumulatorFile, epochFrom, epochTo).ContinueWith((_) =>
            {
                Interlocked.Exchange(ref _canEnterImport, 1);
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
            throw;
        }
        finally
        {
            _eraExporter.ExportProgress -= LogExportProgress;
        }
    }

    private async Task StartImportTask(string source, string accumulatorFile, long from, long to)
    {
        try
        {
            _eraImporter.ImportProgressChanged += LogImportProgress;

            if (_logger.IsInfo) _logger.Info($"Starting history import from {from} to {to}");
            await _eraImporter.Import(
                source,
                from,
                to,
                accumulatorFile,
                _processExit.Token);
            if (_logger.IsInfo) _logger.Info($"Finished history import from {from} to {to}");
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Error($"A running import job was cancelled. Imported archives from '{source}' might be in an incomplete state.");
        }
        catch (EraException e)
        {
            _logger.Error("Import error", e);
        }
        catch (Exception e)
        {
            _logger.Error("Import error", e);
            throw;
        }
        finally
        {
            _eraImporter.ImportProgressChanged -= LogImportProgress;
        }
    }


    public ResultWrapper<string> VerifyHistory(string eraSource, string accumulatorFile)
    {

        if (!_fileSystem.Directory.Exists(eraSource))
            //TODO consider if this is too sensitive information
            return ResultWrapper<string>.Fail($"The directory does not exists.");
        if (!_fileSystem.File.Exists(accumulatorFile))
            //TODO consider if this is too sensitive information
            return ResultWrapper<string>.Fail($"The accumulator file does not exists.");
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
            _eraImporter.VerificationProgress += LogVerificationProgress;
            if (_logger.IsInfo) _logger.Info($"Starting history verification in '{eraSource}'");
            await _eraImporter.VerifyEraFiles(eraSource, accumulatorFile, _processExit.Token);
            if (_logger.IsInfo) _logger.Info($"Succesfully verified all {_eraExporter.NetworkName} archives in '{eraSource}'");
        }
        catch (EraVerificationException e)
        {
            if (_logger.IsInfo) _logger.Info($"History verification failed: {e.Message}");
        }
        catch (Exception e) when (e is EraException or FormatException)
        {
            _logger.Error($"History verification failed: {e.Message}");
        }
        catch (Exception e)
        {
            _logger.Error("History verification failed.", e);
            throw;
        }
        finally
        {
            _eraImporter.VerificationProgress -= LogVerificationProgress;
        }
    }

    private void LogImportProgress(object sender, ImportProgressChangedArgs args)
    {
        if (_logger.IsInfo)
            _logger.Info($"Import progress: | {args.TotalBlocksProcessed,10}/{args.TotalBlocks} blocks  |  {args.EpochProcessed}/{args.TotalEpochs} epochs  |  elapsed {args.Elapsed:hh\\:mm\\:ss}");
    }
    private void LogExportProgress(object sender, ExportProgressArgs args)
    {
        if (_logger.IsInfo)
            _logger.Info($"Export progress: {args.TotalBlocksProcessed,10}/{args.TotalBlocks} blocks  |  elapsed {args.Elapsed:hh\\:mm\\:ss}  |  {args.BlockProcessedSinceLast / args.ElapsedSinceLast.TotalSeconds,10:0.00} Blk/s  |  {args.TxProcessedSinceLast / args.ElapsedSinceLast.TotalSeconds,10:0.00} tx/s");
    }

    private void LogVerificationProgress(object sender, VerificationProgressArgs args)
    {
        if (_logger.IsInfo)
            _logger.Info($"Verification progress: {args.Processed,10}/{args.TotalToProcess} archives  |  elapsed {args.Elapsed:hh\\:mm\\:ss}  |  {args.Processed / args.Elapsed.TotalSeconds,10:0.00} archives/s");
    }
}