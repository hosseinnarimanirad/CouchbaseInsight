using System.Text;
using Couchbase;
using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

/// <summary>One endpoint the SDK actually holds a socket to, taken from the cluster map.</summary>
public sealed record ClusterNode(string Service, string Remote, string State);

/// <summary>
/// A connect failure with the diagnostic report attached. <see cref="Exception.Message"/> stays
/// short enough for the status bar; <see cref="Report"/> carries the seed list, the node addresses
/// the SDK resolved, and the unwrapped exception chain.
/// </summary>
public sealed class ClusterConnectException : Exception
{
    public ClusterConnectException(string message, string report, Exception? inner = null)
        : base(message, inner) => Report = report;

    public string Report { get; }
}

/// <summary>
/// Builds the text shown in the diagnostics pane. The point of the report is the comparison
/// between the seed hosts you typed and the node addresses that came back in the cluster map:
/// the seeds only bootstrap the connection, after which the SDK talks to whatever addresses the
/// cluster advertises for itself. When those differ and don't resolve, every symptom looks like
/// an unexplained timeout.
/// </summary>
public static class ConnectionDiagnostics
{
    /// <summary>
    /// Reads the endpoints from the SDK's own diagnostics. Passive — it reports on existing
    /// sockets rather than opening new ones, so it cannot hang a failing connect.
    /// </summary>
    public static async Task<IReadOnlyList<ClusterNode>> ResolvedNodesAsync(ICluster cluster)
    {
        try
        {
            var report = await cluster.DiagnosticsAsync();
            return report.Services
                .SelectMany(service => service.Value.Select(endpoint => new ClusterNode(
                    service.Key,
                    endpoint.Remote ?? "(unknown)",
                    endpoint.State?.ToString() ?? "(unknown)")))
                .OrderBy(node => node.Service, StringComparer.Ordinal)
                .ThenBy(node => node.Remote, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            return new[] { new ClusterNode("(diagnostics failed)", ex.GetType().Name, ex.Message) };
        }
    }

    public static string BuildReport(
        ConnectionSettings settings,
        ConnectionStringInfo info,
        IReadOnlyList<ClusterNode>? resolvedNodes,
        Exception? error)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Connection string : {settings.ConnectionString}");
        sb.AppendLine($"Scheme            : {info.Scheme}://");
        sb.AppendLine($"User              : {settings.Username}");
        sb.AppendLine($"Network resolution: " +
                      (settings.UseNetworkResolutionExternal ? "external" : "default") +
                      " (never auto — see CouchbaseService.ConnectAsync)");
        sb.AppendLine();

        AppendSeeds(sb, info);
        AppendWarnings(sb, info);
        AppendResolvedNodes(sb, info, resolvedNodes);

        if (error is not null)
        {
            sb.AppendLine("Exception chain (outermost first — the last line is the real cause):");
            foreach (var line in ExceptionReport.Describe(error).Split('\n'))
                sb.AppendLine("  " + line.TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendSeeds(StringBuilder sb, ConnectionStringInfo info)
    {
        sb.AppendLine($"Seed hosts ({info.Seeds.Count}) — used only to bootstrap:");
        if (info.Seeds.Count == 0)
            sb.AppendLine("  (none parsed)");
        else
            foreach (var seed in info.Seeds) sb.AppendLine($"  {seed}");
        sb.AppendLine();
    }

    private static void AppendWarnings(StringBuilder sb, ConnectionStringInfo info)
    {
        if (info.Warnings.Count == 0) return;

        sb.AppendLine("Connection string warnings:");
        foreach (var warning in info.Warnings) sb.AppendLine($"  ! {warning}");
        sb.AppendLine();
    }

    private static void AppendResolvedNodes(
        StringBuilder sb, ConnectionStringInfo info, IReadOnlyList<ClusterNode>? resolvedNodes)
    {
        if (resolvedNodes is null)
        {
            sb.AppendLine("Nodes resolved from the cluster map: none — bootstrap never got that far.");
            sb.AppendLine();
            return;
        } 


        sb.AppendLine("Nodes the SDK resolved from the cluster map (these replace the seeds):");
        if (resolvedNodes.Count == 0)
        {
            sb.AppendLine("  (none — the map yielded no node this machine could open a socket to)");
            sb.AppendLine();
            sb.AppendLine(
                "  ! With no nodes there is nowhere to send an operation, so every call fails with " +
                "'Service <name> is either not configured or cannot be reached' regardless of which " +
                "service it asks for. The usual cause is a node publishing an 'external' alternate " +
                "address: the SDK then addresses the whole cluster by the alternate set. Toggle " +
                "'External network' to match the network this machine is actually on.");
            sb.AppendLine();
            return;
        }

        foreach (var node in resolvedNodes)
            sb.AppendLine($"  {node.Service,-12} {node.Remote,-30} {node.State}");

        var seedHosts = info.Seeds.Select(s => s.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unlisted = resolvedNodes
            .Select(node => HostOf(node.Remote))
            .Where(host => host.Length > 0 && !seedHosts.Contains(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unlisted.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                $"  ! {string.Join(", ", unlisted)} {(unlisted.Count == 1 ? "is" : "are")} not in the " +
                "seed list. The cluster advertises its own addresses and the SDK uses those. If they " +
                "don't resolve from this machine, add DNS or hosts entries, or configure alternate " +
                "addresses on the cluster and tick 'External network'.");
        }

        sb.AppendLine();
    }

    private static string HostOf(string remote)
    {
        if (remote.StartsWith('['))
        {
            var close = remote.IndexOf(']');
            return close > 0 ? remote[1..close] : remote;
        }

        var colon = remote.LastIndexOf(':');
        return colon > 0 && remote.IndexOf(':') == colon ? remote[..colon] : remote;
    }
}
