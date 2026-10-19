# CrackDB

CrackDB is a command-line tool for importing wordlists into a database and exporting new wordlists that match specific requirements, most commonly by password length. It is designed for security testing and password research, where wordlists are often very large (hundreds of gigabytes) and need to be merged, deduplicated, and filtered quickly. Passwords are stored in an embedded database, so there is no server to run and the database is a single portable directory.

## Features

- **Import** - Add one or more wordlists in a single command, from files, directories, or wildcard patterns. Duplicates within a file, between files, and against data already stored are removed automatically.
- **Export by length** - Produce a new wordlist containing every password of an exact length, or within a length range such as 8 to 12 characters.
- **Exact lookup** - Check whether a specific password already exists in the database, with script-friendly exit codes.
- **Built for scale** - Tuned for fast, sustained imports of very large wordlists using an embedded RocksDB store with universal compaction, a bulk-load memtable, and a multi-threaded write pipeline.

## Getting Started

### Prerequisites

- [.NET SDK 9.0 or later](https://dotnet.microsoft.com/download)

### Installation

1. Clone the repository:
   ```
   git clone https://github.com/ChrisLPJones/CrackDB.git
   ```
2. Navigate to the project directory:
   ```
   cd CrackDB
   ```
3. Build the solution:
   ```
   dotnet build -c Release
   ```
   The CLI is built from `src/CrackDB.Cli`; run it with `dotnet run --project src/CrackDB.Cli -- <command>` or use the built `crackdb` executable under `src/CrackDB.Cli/bin/Release/net9.0/`.

To run the engine tests:
```
dotnet test
```

To produce a single self-contained executable that requires no .NET installation on the target machine, run the included script. The finished `crackdb.exe` is written to the `dist` folder:
```
.\build-exe.ps1
```

### Usage

Import one or more wordlists into a database. Arguments may be files, a directory, or a wildcard pattern, and `--recursive` descends into subdirectories:
```
crackdb import rockyou.txt --db MyWordlist
crackdb import a.txt b.txt c.txt --db MyWordlist
crackdb import "C:\Wordlists\WPA" --recursive --db MyWordlist
```

Export a new wordlist by length:
```
crackdb export out.txt --db MyWordlist --min 8 --max 12
crackdb export len8.txt --db MyWordlist --length 8
```

Look up an exact password (exit code 0 if found, 2 if not):
```
crackdb lookup "P@ssw0rd123" --db MyWordlist
```

Show statistics, count matches, or optimize the database for reading:
```
crackdb stats --db MyWordlist
crackdb count --db MyWordlist --min 8 --max 12
crackdb compact --db MyWordlist
```

Generate the password hash the web app needs (the password is read from stdin, never from the command line; it prompts with hidden input on a terminal):
```
crackdb hashpw
```

Run `crackdb --help` for the full list of commands and options.

## Web app

`src/CrackDB.Web` serves the same engine over HTTP: a small login-protected UI plus a JSON API for stats, count, lookup, streamed export, and background import / compact jobs with live progress. It is meant to sit behind a TLS-terminating reverse proxy (e.g. Caddy) on the same host; Kestrel binds `http://127.0.0.1:5080` by default and trusts `X-Forwarded-*` from loopback only. Session cookies are HttpOnly, Secure and SameSite=Strict, so the UI must be reached over HTTPS.

RocksDB allows only one process per database directory, so the web app must be the only process holding a database open: do not run the CLI against a database while the web app is serving it.

Configuration is read from `appsettings.json` and environment variables prefixed `CRACKDB_` (use `__` for nesting). There are no defaults for paths or credentials; the app refuses to start if a required one is missing.

| Variable | Required | Meaning |
|---|---|---|
| `CRACKDB_Storage__DataRoot` | yes | Existing directory whose subdirectories are the databases. |
| `CRACKDB_Storage__KeyPath` | yes | Directory for the Data Protection key ring (keep it private, mode 0700) so sessions survive restarts. |
| `CRACKDB_Storage__IncomingRoot` | no | Existing directory that server-side imports are read from (e.g. `/data/incoming`); browser uploads are staged in its `.uploads` subfolder. Unset disables file import and upload (compact still works). |
| `CRACKDB_Compaction__TimeZone` | no | IANA time zone for the weekly auto-compaction (Sunday 00:00 local time), default `Australia/Sydney`; `UTC` also works. DST-aware. An unknown id aborts startup; the host needs the `tzdata` package (`/usr/share/zoneinfo`) for any zone other than `UTC`. |
| `CRACKDB_Upload__MaxBytes` | no | Cap for a browser upload in bytes. Unset or `0` (the default) means no limit; the file is streamed to disk and the upload is refused with 507 if free disk space runs low. A reverse proxy in front may impose its own body-size limit. |
| `CRACKDB_Auth__Username` | yes | Login user name. |
| `CRACKDB_Auth__PasswordHash` | yes | Output of `crackdb hashpw`. |
| `CRACKDB_Auth__ApiKeyHash` | no | Lowercase hex SHA-256 of the API key; unset disables API-key access. |
| `CRACKDB_Auth__LoginAttemptsPerMinute` | no | Login attempts per client IP per minute (default 5). |
| `CRACKDB_ForwardedProxies` | no | Comma/space-separated IP addresses of reverse proxies (other than loopback) trusted to send `X-Forwarded-For`/`-Proto`, e.g. your Caddy host. Default: loopback only. Invalid values abort startup. |
| `CRACKDB_ForwardedNetworks` | no | Same, as CIDRs (e.g. `192.168.50.0/24`). Only list networks where nothing but your proxy can reach the app port. |

Make an API key and its hash (keep the key itself out of config files; only the hash goes in the environment):
```
KEY=$(openssl rand -hex 32); echo "$KEY"; printf '%s' "$KEY" | sha256sum | cut -d' ' -f1
```

Run it (put the variables in a mode-0600 environment file for a service):
```
dotnet run --project src/CrackDB.Web -c Release
```

Endpoints (all but `/healthz` need the session cookie or an `X-API-Key` header; errors are RFC 7807 problem JSON):

- `GET /healthz` - `ok`.
- `GET /api/dbs` - database names.
- `POST /api/dbs` - JSON `{ "name" }` creates a new, empty database. Returns 201 `{ "name" }`; 400 for an invalid name, 409 if it already exists or the database limit (512) is reached.
- `DELETE /api/dbs/{name}` - permanently deletes the database directory. 204 on success; 404 if it does not exist (including a repeat delete); 400 for an invalid name; 409 if an import/compact job is queued or running for it.
- `GET /api/dbs/{name}/stats[?full=true]` - approximate total; `full` adds an exact per-length breakdown (full scan).
- `GET /api/dbs/{name}/count[?min=&max=&length=]` - approximate total, or an exact count when a bound is given.
- `GET /api/dbs/{name}/lookup?word=...` - exact membership. The word is in the URL, so it can appear in proxy access logs.
- `POST /api/dbs/{name}/export` - streamed `.txt` download ordered by length then password; JSON body or form fields `min`, `max`, `length`, `limit`.

Import and compact run as background jobs, one at a time (a bulk import uses several GB of RAM), and jobs are kept in memory only: they are lost on restart, but a re-run is safe because import dedups. Large lists should be copied into `IncomingRoot` and imported by path; the file is streamed, never loaded whole. While a database is importing or compacting, reads of that database return 409 with a plain-language message naming the running job (other databases are unaffected); the UI shows a maintenance banner and says when a queued job is waiting behind another. Job routes (same auth as above; errors are problem JSON):

- `POST /api/jobs/import` - JSON `{ "db", "source", "min"?, "max"? }`. `source` is a path relative to `IncomingRoot` (no `..`, absolute paths, dot-prefixed names or symlinks); `db` is created if it does not exist. Returns 202 `{ "id" }`.
- `POST /api/jobs/upload` - `multipart/form-data` with the fields `db`, optional `min`/`max`, then the `file` part last. The web UI accepts several files and sends them one POST at a time (each import finishes before the next upload), showing upload progress. Streamed to a temporary file and deleted when the job ends; over the cap (when `Upload:MaxBytes` is set) is 413, low free disk space is 507, and only one upload runs at a time (a second gets 409). Lines longer than 8 KiB are skipped and counted in the job's `errors`.
- `POST /api/jobs/compact` - JSON `{ "db" }`; the database must exist (404 otherwise). A running compaction cannot be cancelled. Every database is also compacted automatically each Sunday at 00:00 in the configured time zone (`CRACKDB_Compaction__TimeZone`, default Australia/Sydney) through the same job queue (no catch-up if the server was down at that time).
- `GET /api/jobs` - recent and active jobs; `GET /api/jobs/{id}` - one job (poll this from scripts).
- `GET /api/jobs/{id}/events` - Server-Sent Events progress snapshots, ending after the final one. Session cookie only (`EventSource` cannot send `X-API-Key`).
- `POST /api/jobs/{id}/cancel` - stops an import after the current line (data already read is kept and flushed) or removes a queued job.

Import reports lines read and an approximate count of new entries (before/after estimate), not exact duplicates.

Database names must match `[A-Za-z0-9][A-Za-z0-9._-]{0,63}` and be a RocksDB directory directly under `DataRoot`; anything else is 400 or 404.

## Example

```
> crackdb import rockyou.txt breachlist.txt --db MyWordlist
Importing 2 file(s), 0.53 GB total -> MyWordlist
  lines read      : 58,144,214
  ~new entries    : 51,320,908   (approx; duplicates auto-collapsed)
  ~total in db    : 51,320,908   (approx)
  elapsed         : 6.2s   (9,378,102 lines/s)

> crackdb export wpa_8to12.txt --db MyWordlist --min 8 --max 12
Exported -> 'wpa_8to12.txt'
  criteria   : length 8..12
  written    : 24,908,551 passwords
  elapsed    : 3.1s

> crackdb lookup "hunter2" --db MyWordlist
FOUND    : 'hunter2' is in the database.
```

## Project Structure

- `CrackDB.sln` - Solution containing all projects below.
- `src/CrackDB.Core/` - Class library with the storage engine (`WordlistDatabase.cs`) covering import, export, lookup, and compaction.
- `src/CrackDB.Cli/` - The `crackdb` command-line tool: `Program.cs` (command handling) and `Progress.cs` (live progress bars).
- `src/CrackDB.Web/` - ASP.NET Core web host: read-only API, single-user auth and a static UI (see "Web app" above).
- `tests/CrackDB.Core.Tests/` - xUnit tests for the engine; they only use throwaway `Test_`-prefixed temp databases.
- `tests/CrackDB.Web.Tests/` - xUnit tests for the web app (`WebApplicationFactory`, throwaway `Test_` data roots).
- `build-exe.ps1` - Builds a single self-contained executable of the CLI.

## How It Works

CrackDB stores each password in an embedded RocksDB key-value store under a key of the form `[length][password]`. Because the key begins with the length, all data is physically ordered by length, which makes the supported queries efficient:

1. An exact lookup is a single point read.
2. A single length, or a length range, is one contiguous scan.

Deduplication is automatic. Identical passwords produce identical keys and collapse, both within a single import and against everything already stored.

The import path is tuned for very large wordlists:

- **Universal compaction** rewrites data far less than the default configuration, so a large import does not slow down as the database grows.
- A **bulk-load memtable** and a **multi-threaded, allocation-free write pipeline** keep throughput high.
- Compaction is deferred during import for maximum speed. Run `crackdb compact` afterwards to consolidate the database for fast reads.

Disk space is traded for speed, so a database is larger than the source wordlists. For best performance, keep the source files and the database on a fast local NVMe drive.

## Intended Use

CrackDB is intended for authorized security testing, password-strength research, CTF challenges, and managing your own wordlists. Only use it against systems you own or are explicitly authorized to test.

## Contributing

Contributions are welcome. Feel free to fork the repository and submit a pull request.

## License

This project is licensed under the MIT License. See the LICENSE file for details.
