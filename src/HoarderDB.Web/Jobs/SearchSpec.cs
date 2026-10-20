namespace HoarderDB.Web.Jobs;

/// <summary>A validated scanning-search request (exact case-sensitive lookups never become a spec; they are a point read).</summary>
public sealed record SearchSpec(string Query, bool IgnoreCase, bool Contains);
