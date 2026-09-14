using Xunit;

// The game-running guard is a process-wide switch, and several tests flip it deliberately.
// Running test classes concurrently let one test change it underneath another, which showed up as
// a transfer being allowed when it should have been refused. Serialising the suite removes the
// shared-state race outright; the whole run is still well under a minute.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
