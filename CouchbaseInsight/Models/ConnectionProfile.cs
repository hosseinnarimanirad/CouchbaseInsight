namespace CouchbaseInsight.Models;

/// <summary>
/// A named, locally-saved connection. Mirrors <see cref="ConnectionSettings"/> plus a display
/// name. The password is held in plaintext in memory; it is encrypted (DPAPI) only when persisted
/// by <see cref="Services.IConnectionStore"/>.
/// </summary>
public class ConnectionProfile
{
    public string Name { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Defaults to false. See <see cref="ConnectionSettings.UseNetworkResolutionExternal"/>.</summary>
    public bool UseNetworkResolutionExternal { get; set; }

    public ConnectionSettings ToSettings() => new()
    {
        ConnectionString = ConnectionString,
        Username = Username,
        Password = Password,
        UseNetworkResolutionExternal = UseNetworkResolutionExternal
    };

    // Used as the ComboBox item label.
    public override string ToString() => Name;
}
