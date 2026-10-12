namespace CrackDB.Web.Storage;

/// <summary>The db name is syntactically invalid or escapes the data root (HTTP 400).</summary>
public sealed class InvalidDbNameException(string message) : Exception(message);

/// <summary>No RocksDB directory with that name exists under the data root (HTTP 404).</summary>
public sealed class DbNotFoundException(string message) : Exception(message);
