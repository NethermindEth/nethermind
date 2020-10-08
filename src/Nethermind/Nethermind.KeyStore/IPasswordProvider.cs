using System.Security;

namespace Nethermind.KeyStore
{
    public interface IPasswordProvider
    {
        SecureString GetBlockAuthorPassword();

        SecureString GetPassword(int keyStoreConfigPasswordIndex);

        SecureString GetPasswordFromConsole();
    }
}
