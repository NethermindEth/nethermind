using System;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Sockets;

namespace Nethermind.DataMarketplace.Infrastructure.Updaters
{
    public class NdmAccountUpdater : INdmAccountUpdater
    {
        private readonly IWebSocketsModule _webSocketsModule;
        private readonly Address _accountAddress;
        private readonly Address? _coldWalletAddress;
        private readonly IBlockProcessor _blockProcessor;
        private readonly IReadOnlyStateProvider _stateProvider;
        private UInt256? _balance;
        private UInt256? _coldBalance;
        private UInt256? _nonce;
        private UInt256? _coldNonce;

        public NdmAccountUpdater(IWebSocketsModule module, Address accountAddress, IBlockProcessor blockProcessor, IReadOnlyStateProvider stateProvider, Address? coldWalletAddress = null)
        {
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _webSocketsModule = module ?? throw new ArgumentNullException(nameof(module));
            _accountAddress = accountAddress ?? throw new ArgumentNullException(nameof(accountAddress));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _coldWalletAddress = coldWalletAddress;


            _blockProcessor.BlockProcessed += UpdateAccountBalance;
            _blockProcessor.BlockProcessed += UpdateAccountNonce;

            if (_coldWalletAddress != null)
            {
                _blockProcessor.BlockProcessed += UpdateColdWalletBalance;
                _blockProcessor.BlockProcessed += UpdateColdWalletNonce;
            }
        }

        private async void UpdateAccountBalance(object? sender, BlockProcessedEventArgs? args)
        {
            UInt256 balanceOnNewBlock = _stateProvider.GetBalance(_accountAddress);

            if(balanceOnNewBlock == _balance)
            {
                return;
            }

            _balance = balanceOnNewBlock;
            await _webSocketsModule.SendAsync(new SocketsMessage("update-balance", "", _balance));
        }
            
        private async void UpdateColdWalletBalance(object? sender, BlockProcessedEventArgs? args)
        {
            UInt256 balanceOnNewBlock = _stateProvider.GetBalance(_coldWalletAddress);

            if(balanceOnNewBlock == _coldBalance)
            {
                return;
            }

            _coldBalance = balanceOnNewBlock;
            await _webSocketsModule.SendAsync(new SocketsMessage("update-cold-balance", "", _coldBalance));
        }

        private async void UpdateAccountNonce(object? sender, BlockProcessedEventArgs? args)
        {
            UInt256 nonceOnNewBlock = _stateProvider.GetNonce(_accountAddress); 

            if(nonceOnNewBlock == _nonce)
            {
                return;
            }

            _nonce = nonceOnNewBlock;
            await _webSocketsModule.SendAsync(new SocketsMessage("update-nonce", "", _nonce));
        }

        private async void UpdateColdWalletNonce(object? sender, BlockProcessedEventArgs? args)
        {
            UInt256 nonceOnNewBlock = _stateProvider.GetNonce(_coldWalletAddress); 

            if(nonceOnNewBlock == _coldNonce)
            {
                return;
            }

            _coldNonce = nonceOnNewBlock;
            await _webSocketsModule.SendAsync(new SocketsMessage("update-cold-nonce", "", _coldNonce));
        }
    }
}
