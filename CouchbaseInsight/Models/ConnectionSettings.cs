namespace CouchbaseInsight.Models;

public class ConnectionSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Set true when connecting via public IP / SSH tunnel (staging).
    /// Maps to NetworkResolution.External in the Couchbase SDK.
    /// </summary>
    public bool UseNetworkResolutionExternal { get; set; }
}
