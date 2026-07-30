namespace CouchbaseInsight.Services;

/// <summary>
/// Stores secrets in the OS-managed credential vault (Windows Credential Manager), keyed by a
/// target name. Non-secret metadata is kept elsewhere; only the secret lives here.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Creates or overwrites the credential for <paramref name="target"/>.</summary>
    void Write(string target, string username, string secret);

    /// <summary>Returns the stored secret, or null when no credential exists for the target.</summary>
    string? ReadSecret(string target);

    /// <summary>Deletes the credential for <paramref name="target"/>. No-op when absent.</summary>
    void Delete(string target);

    /// <summary>Returns the target names of all credentials whose target starts with the prefix.</summary>
    IReadOnlyList<string> Enumerate(string prefix);
}
