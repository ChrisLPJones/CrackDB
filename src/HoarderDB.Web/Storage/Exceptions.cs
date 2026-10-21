namespace HoarderDB.Web.Storage;

/// <summary>The db name is syntactically invalid or escapes the data root (HTTP 400).</summary>
public sealed class InvalidDbNameException(string message) : Exception(message);

/// <summary>No RocksDB directory with that name exists under the data root (HTTP 404).</summary>
public sealed class DbNotFoundException(string message) : Exception(message);

/// <summary>The db is mid-import/compact; reads are refused rather than blocked for hours (HTTP 409).</summary>
public sealed class DbBusyException(string message) : Exception(message);

/// <summary>The import source path is syntactically invalid or escapes the incoming root (HTTP 400).</summary>
public sealed class InvalidSourcePathException(string message) : Exception(message);

/// <summary>The import source does not exist or is not a regular, non-empty file (HTTP 404).</summary>
public sealed class SourceNotFoundException(string message) : Exception(message);

/// <summary>An upload exceeded the configured cap (HTTP 413).</summary>
public sealed class UploadTooLargeException(string message) : Exception(message);

/// <summary>No such job (HTTP 404).</summary>
public sealed class JobNotFoundException(string message) : Exception(message);

/// <summary>File imports/uploads need Storage:IncomingRoot, which is not configured (HTTP 404).</summary>
public sealed class IncomingNotConfiguredException(string message) : Exception(message);

/// <summary>A database with that name already exists (HTTP 409).</summary>
public sealed class DbAlreadyExistsException(string message) : Exception(message);

/// <summary>The data root already holds the maximum number of databases (HTTP 409).</summary>
public sealed class DbLimitReachedException(string message) : Exception(message);

/// <summary>A backfill batch would push a database's import history past its entry cap (HTTP 413); rejected whole, not truncated.</summary>
public sealed class ImportHistoryCapExceededException(string message) : Exception(message);

/// <summary>The length-breakdown sidecar could not be written (job fails with a fixed message; the previous result stays).</summary>
public sealed class BreakdownSaveException(string message, Exception? inner = null) : Exception(message, inner);
