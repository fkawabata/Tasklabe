using Windows.Security.Credentials;

namespace Tasklabe.App.Services;

/// <summary>アクセストークンを Windows 資格情報マネージャーに保管し、次に起動したときのサインインに使う（要件 F-AUTH-02、NF-06）。</summary>
public sealed class TokenStore(string host)
{
    private string Resource => $"Tasklabe:{host}";

    public string? Load()
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.FindAllByResource(Resource).FirstOrDefault();
            if (credential is null)
            {
                return null;
            }

            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490)) // ELEMENT_NOT_FOUND
        {
            return null;
        }
    }

    public void Save(string login, string token)
    {
        Delete();
        new PasswordVault().Add(new PasswordCredential(Resource, login, token));
    }

    public void Delete()
    {
        try
        {
            var vault = new PasswordVault();
            foreach (var credential in vault.FindAllByResource(Resource))
            {
                vault.Remove(credential);
            }
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490))
        {
        }
    }
}
