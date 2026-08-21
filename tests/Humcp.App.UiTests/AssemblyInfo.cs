using Xunit;

// This suite drives one real window on the real desktop and sends real keystrokes to it. Two of
// these running at once would fight over the foreground and type into each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
