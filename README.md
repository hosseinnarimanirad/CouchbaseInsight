# CouchbaseInsight

A WPF desktop tool for exploring Couchbase clusters: browse scopes and collections, infer document
schemas, inspect distinct field values, and run N1QL queries.

## The window

The app draws its own title bar. It carries the wordmark, a status chip naming the cluster and
bucket you are attached to, the toggle for the connection form, and the theme switch.

**Connecting.** The connection form is open on launch and folds itself away on a successful
connect — after that it is a screenful of details nobody reads again that session. The
*Connection* button in the title bar brings it back; a failed connect brings it back on its own,
with the diagnostics report underneath it.

**Choosing what to look at.** Bucket, scope, collection and discriminator sit on one row below,
in that order, because each one is only answerable once the one before it is.

**Scan limit.** The row under the picker sets how many documents Load classes, Analyze and the
distinct-values list read: the collection's first N in primary-key order (default 50,000; 0 or
empty reads the whole collection). The limit is applied inside a subquery, before any WHERE, so on
a collection with only a primary index the server stops after N documents instead of fetching all
of them to test the condition. A class that only occurs past the first N is not listed — raise
the limit, or clear it, when that matters. The Query tab is not limited. The discriminator
defaults to `class`. Each class in the list shows its document count and its share of the
documents read.

**Collection summary.** Picking a collection shows its document count at once, read from the
collection's item count rather than by a scan.

**Batched whole-collection reads.** With the scan limit cleared, Load classes, and Analyze with
*Read every document* on, read the collection in batches instead of one long query. The batch size
is set in **Options** (gear in the title bar; default 1,000,000, kept in
`%APPDATA%\CouchbaseInsight\options.json`), and the export uses it too. Each batch is a key range:
a keys-only query on the primary index finds where it ends, then the batch is counted or read.
A progress line under the picker shows documents done of the total, %, batch N of M, docs/s and
time left. The class list fills in with running counts after each batch. **Cancel** stops the read
and keeps what was counted so far, marked as partial.

**Export collection.** *Export…* (beside Load classes) opens a dialog for the date field whose
first/last value is reported per class (guessed from the collection) and the file to save to, or
offers to resume an unfinished export of the same collection. It reads *every* document of the collection, in batches by document key
(default 1,000,000; `META().id > last key ORDER BY META().id`, so no batch re-reads earlier ones),
and writes one JSON summary: per class its count, %, min/max of the chosen date field (raw and UTC),
and per field its types, seen, presence %, requirement, flags and distinct values — all of them
when there are 50 or fewer, otherwise the 50 most frequent with `complete: false`. After each
batch the running summary is saved to `%APPDATA%\CouchbaseInsight\exports\`, so an export that is
cancelled or fails resumes from the last finished batch the next time *Export collection…* is
pressed on the same collection and discriminator.

**Open export.** *Open export…* shows a saved export in the Schema explorer without querying
the cluster, and works with no connection. The class list carries the export's whole-collection
counts and % (hover a class for its date range); selecting a class shows its schema; selecting a field
shows its saved distinct values with counts, marked "Top 50 of …" when the list is not complete.
It also opens an unfinished export's checkpoint from `%APPDATA%\CouchbaseInsight\exports\`, as a
report of the documents read so far. *Close file*, Load classes, or picking another bucket, scope
or collection goes back to the cluster.

**Schema explorer.** Classes on the left, filterable; the inferred schema on the right. Each field
shows its parent path dimmed in front of its own name, one badge per JSON type it was seen as, a
bar for how much of the sample contained it, and a flag when it collides with another field only by
case or was seen as more than one type. The headline numbers — fields, required, flagged, documents
sampled — are pills in the pane header. Clicking a field lists the values it actually holds.
Double-clicking a class analyses it.

**Query.** The builder composes `SELECT … FROM … WHERE …` and shows the exact statement it will
send; *Edit as SQL++* hands that statement to a free-text editor for everything the builder cannot
express (JOIN, ORDER BY, LIMIT, subqueries). The clause editor and the result pane are both
syntax-highlighted, and Ctrl+F searches a result. **F5** or **Ctrl+Enter** runs the query; a
running one can be cancelled.

**Theme.** Light and dark, switched from the title bar. The choice is remembered in
`%APPDATA%\CouchbaseInsight\ui.json`; before there is one, the app follows the Windows app theme.

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
