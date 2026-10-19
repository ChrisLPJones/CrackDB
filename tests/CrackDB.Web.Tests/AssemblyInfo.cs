using Xunit;

// Test classes in this assembly open real RocksDB handles through CrackDbWebFactory (ServerGC
// heaps, a 256 MB block cache, write buffers per handle). xUnit's default cross-class parallelism
// would otherwise start several of these concurrently in one process, which does not match
// production (a single DbManager, one job worker at a time) and can itself create a real, if
// brief, local memory spike that the HeadroomGuard job-start check then correctly refuses against
// - a test-induced false positive, not a flaw in the headroom logic. Running test classes serially
// here removes that self-inflicted contention.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
