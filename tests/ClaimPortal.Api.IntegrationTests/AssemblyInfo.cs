using Xunit.Sdk;
using Xunit.v3;

// The suite owns Docker containers and real broker queues. xUnit treats test classes in
// different collections as parallel by default; disable it so the resource lifetime stays
// predictable and failures point at the test, not at another test's container teardown.
[assembly: Parallelization(Mode = ParallelMode.None)]
