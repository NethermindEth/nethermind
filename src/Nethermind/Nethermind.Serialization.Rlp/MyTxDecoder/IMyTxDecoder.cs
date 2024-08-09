using Nethermind.Core;

namespace Nethermind.Serialization.Rlp.MyTxDecoder;

interface IMyTxDecoder<T> : IRlpStreamDecoder<T>, IRlpValueDecoder<T> where T: Transaction, new() { }
