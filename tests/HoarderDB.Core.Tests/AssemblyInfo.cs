using Xunit;

// Test classes here open real RocksDB handles and assert on process-wide state (scan-permit limiters,
// thread counts, timing). Running classes serially keeps them from disturbing each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
