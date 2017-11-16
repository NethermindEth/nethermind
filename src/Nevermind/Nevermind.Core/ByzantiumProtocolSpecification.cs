namespace Nevermind.Core
{
    public class ByzantiumProtocolSpecification : IProtocolSpecification
    {
        public bool IsEip2Enabled => true;
        public bool IsEip7Enabled => true;
        public bool IsEip150Enabled => true;
        public bool IsEip155Enabled => true;
        public bool IsEip158Enabled => true;
        public bool IsEip160Enabled => true;
        public bool IsEip170Enabled => true;
        public bool IsEmptyCodeContractBugFixed => true;
        public bool IsEip186Enabled => true;
    }
}