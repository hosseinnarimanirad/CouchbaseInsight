using System.Net;

namespace CouchbaseInsight.Services;

/// <summary>One entry from the seed list, as written and as the SDK will read it.</summary>
public sealed record SeedHost(string AsWritten, string Host, int? Port)
{
    public override string ToString() => Port is null ? Host : $"{Host}:{Port}";
}

/// <summary>The parsed shape of a connection string, plus anything suspicious about it.</summary>
public sealed class ConnectionStringInfo
{
    public required string Scheme { get; init; }
    public required IReadOnlyList<SeedHost> Seeds { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Parses a connection string the way the SDK does and flags the mistakes that surface as a bare
/// timeout rather than a useful error: stray whitespace between hosts, management ports pasted
/// from the web console, empty entries from a trailing comma.
/// </summary>
public static class ConnectionStringInspector
{
    private const int KvPort = 11210;
    private const int KvTlsPort = 11207;

    // Ports people paste from the Couchbase web console, none of which bootstrap the SDK.
    private static readonly Dictionary<int, string> HttpPorts = new()
    {
        [8091] = "management", [18091] = "management (TLS)",
        [8092] = "views", [18092] = "views (TLS)",
        [8093] = "query", [18093] = "query (TLS)"
    };

    public static ConnectionStringInfo Inspect(string? connectionString)
    {
        var warnings = new List<string>();
        var work = (connectionString ?? string.Empty).Trim();

        if (work.Length == 0)
        {
            warnings.Add("The connection string is empty.");
            return new ConnectionStringInfo
            {
                Scheme = "couchbase", Seeds = Array.Empty<SeedHost>(), Warnings = warnings
            };
        }

        var scheme = TakeScheme(ref work, warnings);
        work = StripParameters(work);
        work = StripCredentials(work);

        var seeds = new List<SeedHost>();
        foreach (var part in work.Split(','))
        {
            var trimmed = part.Trim();

            if (trimmed.Length == 0)
            {
                warnings.Add("Empty entry in the host list — a doubled or trailing comma.");
                continue;
            }

            if (trimmed.Length != part.Length)
            {
                warnings.Add(
                    $"'{part}' is padded with whitespace. The SDK splits the list on commas without " +
                    "trimming, so this becomes a host name with a space in it and never resolves. " +
                    "Remove the spaces after the commas.");
            }

            var (host, port) = SplitHostAndPort(trimmed);
            seeds.Add(new SeedHost(part, host, port));
        }

        InspectPorts(scheme, seeds, warnings);
        InspectHosts(scheme, seeds, warnings);

        return new ConnectionStringInfo { Scheme = scheme, Seeds = seeds, Warnings = warnings };
    }

    private static string TakeScheme(ref string work, List<string> warnings)
    {
        var separator = work.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            warnings.Add("No scheme — write it explicitly as couchbase:// (or couchbases:// for TLS).");
            return "couchbase";
        }

        var scheme = work[..separator].ToLowerInvariant();
        work = work[(separator + 3)..];

        if (scheme is not ("couchbase" or "couchbases"))
        {
            warnings.Add(
                $"'{scheme}://' is not a Couchbase scheme. Use couchbase:// for plain connections " +
                "or couchbases:// for TLS.");
        }

        return scheme;
    }

    private static string StripParameters(string work)
    {
        var query = work.IndexOf('?');
        return query < 0 ? work : work[..query];
    }

    private static string StripCredentials(string work)
    {
        var at = work.LastIndexOf('@');
        return at < 0 ? work : work[(at + 1)..];
    }

    // Handles bracketed IPv6 ([::1]:11210) and leaves bare IPv6 alone — a bare address has several
    // colons, none of which is a port separator.
    private static (string Host, int? Port) SplitHostAndPort(string entry)
    {
        if (entry.StartsWith('['))
        {
            var close = entry.IndexOf(']');
            if (close < 0) return (entry, null);

            var host = entry[1..close];
            var rest = entry[(close + 1)..];
            return rest.StartsWith(':') && int.TryParse(rest[1..], out var bracketedPort)
                ? (host, bracketedPort)
                : (host, null);
        }

        var colon = entry.LastIndexOf(':');
        if (colon <= 0 || entry.IndexOf(':') != colon) return (entry, null);

        return int.TryParse(entry[(colon + 1)..], out var port)
            ? (entry[..colon], port)
            : (entry, null);
    }

    private static void InspectPorts(string scheme, List<SeedHost> seeds, List<string> warnings)
    {
        var expected = scheme == "couchbases" ? KvTlsPort : KvPort;

        foreach (var seed in seeds.Where(s => s.Port is not null))
        {
            if (HttpPorts.TryGetValue(seed.Port!.Value, out var role))
            {
                warnings.Add(
                    $"Port {seed.Port} on '{seed.Host}' is the {role} port. The SDK bootstraps over " +
                    $"the key-value port, so this connection will fail — drop the port or use {expected}.");
            }
            else if (seed.Port != expected)
            {
                warnings.Add(
                    $"Port {seed.Port} on '{seed.Host}' is not the key-value port for {scheme}:// " +
                    $"(expected {expected}).");
            }
        }

        if (seeds.Count > 1 && seeds.Any(s => s.Port is not null) && seeds.Any(s => s.Port is null))
            warnings.Add("Some hosts carry a port and others don't — make the list consistent.");
    }

    private static void InspectHosts(string scheme, List<SeedHost> seeds, List<string> warnings)
    {
        var duplicates = seeds
            .GroupBy(s => s.Host, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
            warnings.Add($"Repeated host(s) in the seed list: {string.Join(", ", duplicates)}.");

        if (scheme == "couchbases" && seeds.Any(s => IPAddress.TryParse(s.Host, out _)))
        {
            warnings.Add(
                "couchbases:// with IP addresses requires every node certificate to list those IPs " +
                "in its subject-alternative-name list; a hostname-only certificate is rejected.");
        }
    }
}
