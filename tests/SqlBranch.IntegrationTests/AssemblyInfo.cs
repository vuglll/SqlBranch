// The tests share one Docker host and assert that no SqlBranch container is left, so they run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
