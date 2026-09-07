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
3. Build the project:
   ```
   dotnet build -c Release
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

Run `crackdb --help` for the full list of commands and options.

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

- `Program.cs` - Command-line interface and command handling.
- `WordlistDatabase.cs` - Storage engine covering import, export, lookup, and compaction.
- `Progress.cs` - Live progress bars for import and export.
- `build-exe.ps1` - Builds a single self-contained executable.

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
