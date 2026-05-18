// Disable xunit parallel test execution across collections — multiple test classes
// open the same .mdb corpus files, and even with FileShare.ReadWrite the OS sometimes
// rejects the second concurrent FileStream when both request FileAccess.ReadWrite.
// The full suite runs in <2s sequentially, so this costs nothing.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
