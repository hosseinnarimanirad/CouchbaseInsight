# CouchbaseInsight

A WPF desktop tool for exploring Couchbase clusters: browse scopes and collections, infer document
schemas, inspect distinct field values, and write, save and run N1QL queries, with writes kept behind
a switch and a confirmation.

## The window

The app draws its own title bar. It carries the wordmark, a status chip naming the cluster and
bucket you are attached to, the toggle for the connection form, and the theme switch.

**Connecting.** The connection form is open on launch and folds itself away on a successful
connect — after that it is a screenful of details nobody reads again that session. The
*Connection* button in the title bar brings it back; a failed connect brings it back on its own,
with the diagnostics report underneath it.

**Choosing what to look at.** Bucket, scope, collection and discriminator sit on one row below,
in that order, because each one is only answerable once the one before it is.

**Load (two steps).** *Load* counts every class over the whole collection on the server, range by
range (several at once), and downloads no document: the class list shows exact counts and %.
In the same pass it keeps up to 1,000 document keys per class, then fetches each class's sample
by key (no search) to list its fields with their sample presence, so selecting a class is instant.
Each class shows how far it has got: ○ counted, ◐ fields listed, ◑ partly analyzed, ● analyzed.
*Analyze all classes* ticks every field that never holds an object or an array, in every class,
and counts them all in one pass, after which Export holds the whole collection. Tick the fields to analyze (*Scalars* ticks all but objects and arrays; *All*,
*None*; ticks are by field name, so they carry across classes), then *Analyze selected*, or press
*Top 50* on one field's row: the server counts those fields in every class at once, over the whole
collection or its first N documents while the scan-limit switch is on, in one pass, and returns
only the counts — exact presence, the 50 most frequent values with
counts, distinct count, min and max. Analyzed fields get a ✓ and their numbers become exact. A
field with more than 1,000 distinct values in some range is marked approximate. On a server
without window functions the per-field totals are worked out in the app instead.

**Document keys.** The keys Load keeps per class give its key shapes (`acc::{uuid}`,
`otp::{n}`), shown when hovering a class and saved with a few example keys. The pane beside the
values lists documents of the selected class's sample (or a Deep load's oldest and newest) with
their JSON; *Copy key* and *Open in Query tab* (`SELECT … USE KEYS`) work on the one selected.
Each analyzed value comes with one document that holds it: select a value and press *Open
example*. Keys are shown and exported as they are, personal data included (e.g. `otp::<mobile>`).

**Deep load.** The other way: download the documents and summarise everything in the app. One read gathers everything. *Deep load* reads the collection's first N
documents by key while the **Scan limit** switch beside it is on, or the whole collection when it
is off (the switch is remembered between sessions; the number starts from Options; default on, 50,000)
in key-ordered pages of the Options batch size (gear in the title bar; default 1,000,000, kept in
`%APPDATA%\CouchbaseInsight\options.json`). Each page is cut into key ranges by keys-only queries on
the primary index, and the ranges are read at the same time (*Parallel reads* in Options, default
4), each as `META().id > after AND META().id <= end` with no sorting, so none is read twice or skipped. From those documents it
builds, per class (the discriminator, default `class`), the count and %, the first/last value of
the date field (guessed from the collection: `createdts`, `createdAt`, …), and per field its types,
seen, presence %, requirement, flags and values — all of them when there are 50 or fewer,
otherwise the 50 most frequent. Selecting a class or a field afterwards reads nothing. While it
runs, the window is dimmed under a card with progress (documents done of the total, batch N of M,
docs/s, time left) and **Cancel**, which keeps and shows what was read so far. After each page the
running summary is saved to `%APPDATA%\CouchbaseInsight\exports\`, so a load that is cancelled or
fails offers to resume next time. When it finishes, the caption checks the total against the
collection's count.

The same read also gathers, with no extra query (the page query only adds `META().expiration`
and `META().cas`): per class the documents with a TTL, the key shapes (`user::{uuid}`, `otp::{n}`),
the last write, and the oldest and newest document whole (not masked), plus which fields only one
of the two has; per field an example value, the "other" and "missing" counts beside its top
values, and the first and last date it was seen. Fields inside arrays are listed as
`items[].amount`. Hover a class for its TTL, key shapes and dates.

**Exact counts.** After a Load with a scan limit, *Count every class exactly* (under the class
list caption) counts every class over the whole collection on the server, one batch of keys at a
time, so only counts travel back. The list then shows the exact counts and %, and classes that
none of the documents read had appear with a count only. Export includes them.

**Gentle mode** (Options; default on). For running against the cluster the live application
uses: Load, Analyze and Deep load go one batch at a time with a 2-second pause between batches,
and each batch query runs with one fetch thread on the server, the server's default fetch sizes
and a 5-minute server-side timeout, so a stalled query is stopped instead of holding resources.
Off: several batches at once (*Parallel reads*) with large fetch groups, for the fastest read.
The batch size (default 100,000; 100K, 250K, 500K or 1M) sets how many keys each query covers.

**Collection summary.** Picking a collection shows its document count at once, read from the
collection's item count rather than by a scan.

**Export / Open export.** *Export…* saves what Load gathered as a JSON file; it reads nothing.
*Open export…* shows a saved file (or an unfinished load's checkpoint) in the explorer without a
connection; *Close file*, Load, or picking another collection goes back to the cluster.

**Schema explorer.** Classes on the left, filterable; the inferred schema on the right. Each field
shows its parent path dimmed in front of its own name, one badge per JSON type it was seen as, a
bar for how much of the sample contained it, and a flag when it collides with another field only by
case or was seen as more than one type. The headline numbers — fields, required, flagged, documents
read — are pills in the pane header. Clicking a field lists its most frequent values with counts.


**Query.** The builder composes `SELECT … FROM … WHERE …` and shows the exact statement it will
send; *Edit as SQL++* hands that statement to a free-text editor for everything the builder cannot
express (JOIN, ORDER BY, LIMIT, subqueries). The clause editor and the result pane are both
syntax-highlighted, and Ctrl+F searches a result. **F5** or **Ctrl+Enter** runs the query; a
running one can be cancelled. The pill over the result gives the server's own numbers: rows and
elapsed time for a read, documents changed for a write.

**Saved queries.** The pane on the left of the Query tab lists the queries saved for the connection
picked in the connection form, grouped by folder; the filter box matches name, folder, tag or
statement text. They can be browsed and edited without a connection; only running needs one.
Clicking a query opens it in the SQL++ editor. The row above the editor names it: *Name*, *Folder*
(`fixes/otp`; `/` separates levels) and *Tags* (comma-separated). **Save** (Ctrl+S) writes the
query back under the same identity, so changing its name or folder and saving renames or moves it;
*Save as new* makes a copy, adding "(2)" when the name is taken. A `•` after the title marks unsaved
edits, and opening another query, *New*, or closing the window asks before they are lost.
*Import…* adds the queries of an exported file to the current connection (a name already used in
its folder gets "(2)"); *Export…* saves the queries the list shows, all of them or those the filter
matches. Deleting a connection deletes its saved queries too, after offering to export them.

The queries live in `%APPDATA%\CouchbaseInsight\queries.json`, which holds no secrets. Every change
is written to a temporary file and swapped in, with the previous version kept as
`queries.json.bak`. A file that cannot be read is set aside as `queries.unreadable-<time>.json`
rather than overwritten; a file that is locked makes the list read-only for that session.

**Reads and writes.** A statement is a read only when its first keyword (comments and brackets
skipped) is `SELECT`, `WITH`, `EXPLAIN`, `ADVISE` or `INFER`; anything else — `UPDATE`, `DELETE`,
`INSERT`, `UPSERT`, `MERGE`, `CREATE INDEX`, `EXECUTE`, … — counts as a write, shows a red **WRITE**
badge beside Run, and gets a ✎ in the saved list. Reads are sent with the server's read-only flag,
so a write that got past the check is refused by the cluster itself. A write runs only while the
**Allow writes** switch is on — it is off at launch and turns itself off on every connect and
disconnect — and then only after a confirmation card that shows the whole statement and the cluster
it goes to. Enter does not confirm it; Escape cancels. Cancelling a write that is already running
only stops the wait in the app: the cluster may still have applied some or all of it.

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
