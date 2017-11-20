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
    }
}