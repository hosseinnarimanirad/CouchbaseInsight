# CouchbaseInsight

A WPF desktop tool for exploring Couchbase clusters: browse scopes and collections, infer document
schemas, inspect distinct field values, and run N1QL queries.

## Setup

Credentials are never stored in the repository. Pick one of the two supported ways to supply them.

**User secrets (recommended).** Nothing lands in the working tree at all:

```bash
cd CouchbaseInsight
dotnet user-secrets set "Couchbase:ConnectionString" "couchbases://your-host"
dotnet user-secrets set "Couchbase:Username" "your-user"
dotnet user-secrets set "Couchbase:Password" "your-password"
```

**Local `appsettings.json`.** Copy the template and edit your copy:

```bash
cp CouchbaseInsight/appsettings.template.json CouchbaseInsight/appsettings.json
```

`CouchbaseInsight/appsettings.json` is gitignored, so your edits stay local. The file is optional —
the app starts fine without it.

Set `UseNetworkResolutionExternal` to `true` when connecting through a public IP or SSH tunnel, and
`false` when you are on the same network as the nodes. The flag is passed to the SDK in both
directions — `NetworkResolution.External` or `NetworkResolution.Default`, never `Auto`.

That matters on any cluster where a node publishes an `external` alternate address. Under `Auto` the
SDK may decide to address the *whole* cluster by those alternates, so a LAN connection to reachable
internal IPs quietly retargets a public IP it cannot open a key-value socket to. Bootstrap still
reports success, the SDK ends up holding no nodes, and every call afterwards fails with
`Service mgmt is either not configured or cannot be reached` — an error that names the service it
happened to ask for first and says nothing about the real cause. The app now rejects that state at
connect time and says so in the diagnostics pane instead.

## Where credentials live

Either source above is only a *first-run seed*. On startup the app migrates it into a saved
connection profile, after which:

- **Passwords** go to the Windows Credential Manager as generic credentials under the target prefix
  `CouchbaseInsight:` — encrypted per-user by the OS, and auditable via Control Panel → Credential
  Manager.
- **Non-secret metadata** (profile name, connection string, username, external-resolution flag) goes
  to `%APPDATA%\CouchbaseInsight\connections.json`.

Neither location is inside the repository. Deleting a profile in the app also removes its vault
entry.

## Build

```bash
dotnet build
```

Requires the .NET 9 SDK on Windows (WPF).
