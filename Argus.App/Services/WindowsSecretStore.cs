using Argus.Core.Services;
using Windows.Security.Credentials;

namespace Argus.App.Services;

public sealed class WindowsSecretStore : ISecretStore
{
    private const string ResourceName = "Argus.AiProviders";
    private readonly PasswordVault vault = new();

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var credential = vault.Retrieve(ResourceName, key);
            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        RemoveIfExists(key);
        vault.Add(new PasswordCredential(ResourceName, key, value));
        return Task.CompletedTask;
    }

    public Task RemoveSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        RemoveIfExists(key);
        return Task.CompletedTask;
    }

    private void RemoveIfExists(string key)
    {
        try
        {
            vault.Remove(vault.Retrieve(ResourceName, key));
        }
        catch
        {
            // Credential Locker throws when a key does not exist.
        }
    }
}
