namespace Nevermind.Core
{
    /// <summary>
    /// https://github.com/ethereum/EIPs
    /// </summary>
    public interface IProtocolSpecification
    {
        /// <summary>
        /// CREATE instruction cost set to 32000 (previously 0)
        /// Failing init does not create an empty code contract
        /// Difficulty adjustment changed
        /// </summary>
        bool IsEip2Enabled { get; }

        /// <summary>
        /// DELEGATECALL instruction added
        /// </summary>
        bool IsEip7Enabled { get; }

        /// <summary>
        /// Change difficulty adjustment to target mean block time including uncles
        /// </summary>
        bool IsEip100Enabled { get; }

        /// <summary>
        /// REVERT instruction in the Ethereum Virtual Machine
        /// </summary>
        bool IsEip140Enabled { get; }

        /// <summary>
        /// Gas cost of IO operations increased
        /// </summary>
        bool IsEip150Enabled { get; }

        /// <summary>
        /// Chain ID in signatures
        /// </summary>
        bool IsEip155Enabled { get; }

        /// <summary>
        /// State clearing
        /// </summary>
        bool IsEip158Enabled { get; }

        /// <summary>
        /// EXP cost increase
        /// </summary>
        bool IsEip160Enabled { get; }

        /// <summary>
        /// Code size limit
        /// </summary>
        bool IsEip170Enabled { get; }

        /// <summary>
        /// Block reward decreased to 3 ETH
        /// </summary>
        bool IsEip186Enabled { get; }

        /// <summary>
        /// Precompiled contracts for addition and scalar multiplication on the elliptic curve alt_bn128
        /// </summary>
        bool IsEip196Enabled { get; }

        /// <summary>
        /// Precompiled contracts for optimal ate pairing check on the elliptic curve alt_bn128
        /// </summary>
        bool IsEip197Enabled { get; }

        /// <summary>
        /// Precompiled contract for bigint modular exponentiation
        /// </summary>
        bool IsEip198Enabled { get; }

        /// <summary>
        /// New opcodes: RETURNDATASIZE and RETURNDATACOPY
        /// </summary>
        bool IsEip211Enabled { get; }

        /// <summary>
        /// New opcode STATICCALL
        /// </summary>
        bool IsEip214Enabled { get; }

        /// <summary>
        /// Difficulty Bomb Delay and Block Reward Reduction
        /// </summary>
        bool IsEip649Enabled { get; }

        /// <summary>
        /// Embedding transaction return data in receipts
        /// </summary>
        bool IsEip658Enabled { get; }
    }
}