using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
{
    public class TxPoolModule : ModuleBase, ITxPoolModule
    {
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly IJsonRpcModelMapper _modelMapper;

        public TxPoolModule(IConfigProvider configurationProvider, ILogManager logManager, IJsonSerializer jsonSerializer,
            IBlockchainBridge blockchainBridge, IJsonRpcModelMapper modelMapper) : base(configurationProvider, logManager, jsonSerializer)
        {
            _blockchainBridge = blockchainBridge;
            _modelMapper = modelMapper;
        }
        
        public ResultWrapper<TransactionPoolStatus> txpool_status()
            => ResultWrapper<TransactionPoolStatus>.Success(
                _modelMapper.MapTransactionPoolStatus(_blockchainBridge.GetTransactionPoolInfo()));

        public ResultWrapper<TransactionPoolContent> txpool_content()
            => ResultWrapper<TransactionPoolContent>.Success(
                _modelMapper.MapTransactionPoolContent(_blockchainBridge.GetTransactionPoolInfo()));

        public ResultWrapper<TransactionPoolInspection> txpool_inspect()
            => ResultWrapper<TransactionPoolInspection>.Success(
                _modelMapper.MapTransactionPoolInspection(_blockchainBridge.GetTransactionPoolInfo()));
    }
}