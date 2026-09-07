using Jane.App.Composition;

namespace Jane.App.Tests;

/// <summary>
/// Only one Jane at a time.
/// </summary>
/// <remarks>
/// <para>
/// Jane had no instance guard at all, and a second copy is not merely redundant -- it is actively
/// destructive. Two copies install two low-level keyboard hooks on the same key, open the same
/// capture device against each other, and draw two pills at the same bottom-centre anchor. That
/// last one is what surfaced it: a screenshot of the resting pill with two different strings drawn
/// on top of each other, which a single overlay cannot produce because it holds a single text
/// element.
/// </para>
/// <para>
/// The guard is a named mutex rather than a process scan, because a scan races: two copies
/// launched together both look around, both see nothing, and both continue. Session-scoped
/// (<c>Local\</c>) rather than machine-wide, since Jane is a per-user tray app and two users
/// signed into the same machine are entitled to one each.
/// </para>
/// </remarks>
public sealed class SingleInstanceTests
{
    private static string UniqueName() => $"jane-test-{Guid.NewGuid():N}";

    [Fact]
    public void TheFirstInstanceOwnsIt()
    {
        var name = UniqueName();

        using var first = SingleInstance.Acquire(name);

        Assert.True(first.IsOwner);
    }

    [Fact]
    public void ASecondInstanceDoesNotOwnIt()
    {
        var name = UniqueName();

        using var first = SingleInstance.Acquire(name);
        using var second = SingleInstance.Acquire(name);

        Assert.True(first.IsOwner);
        Assert.False(second.IsOwner);
    }

    [Fact]
    public void ReleasingTheOwnerLetsTheNextInstanceIn()
    {
        var name = UniqueName();

        var first = SingleInstance.Acquire(name);
        Assert.True(first.IsOwner);
        first.Dispose();

        using var second = SingleInstance.Acquire(name);

        // Jane quitting has to hand the name back, or the next launch after a restart is refused
        // by a mutex nobody holds.
        Assert.True(second.IsOwner);
    }

    [Fact]
    public void DifferentNamesDoNotCollide()
    {
        using var one = SingleInstance.Acquire(UniqueName());
        using var two = SingleInstance.Acquire(UniqueName());

        Assert.True(one.IsOwner);
        Assert.True(two.IsOwner);
    }

    [Fact]
    public async Task TheOwnerIsToldWhenAnotherCopyTriesToStart()
    {
        var name = UniqueName();
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var first = SingleInstance.Acquire(name);
        first.SecondInstanceAttempted += (_, _) => signalled.TrySetResult();

        // The second copy exits, but silently exiting looks identical to the launch doing nothing.
        // Telling the running copy lets it surface itself instead.
        using (var second = SingleInstance.Acquire(name))
        {
            Assert.False(second.IsOwner);
            second.NotifyOwner();
        }

        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void NotifyingIsHarmlessWhenNobodyIsListening()
    {
        var name = UniqueName();

        // The owner may have quit between the second copy's acquire and its notify. That is a
        // race Jane cannot prevent, so it has to be survivable rather than an unhandled throw on
        // a background thread.
        using var orphan = SingleInstance.Acquire(name);
        orphan.NotifyOwner();
    }

    [Fact]
    public void TheNameCanBeOverriddenByTheEnvironment()
    {
        // A test that launches a real Jane in a child process cannot pass it an argument, and
        // without this the child loses the claim to the developer's own running copy and exits
        // before it measures anything.
        var name = UniqueName();
        var previous = Environment.GetEnvironmentVariable(SingleInstance.EnvName);
        try
        {
            Environment.SetEnvironmentVariable(SingleInstance.EnvName, name);

            using var viaEnvironment = SingleInstance.Acquire();
            using var sameNameDirectly = SingleInstance.Acquire(name);

            Assert.True(viaEnvironment.IsOwner);
            Assert.False(sameNameDirectly.IsOwner);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SingleInstance.EnvName, previous);
        }
    }

    [Fact]
    public void AnExplicitNameBeatsTheEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable(SingleInstance.EnvName);
        try
        {
            Environment.SetEnvironmentVariable(SingleInstance.EnvName, UniqueName());

            using var viaEnvironment = SingleInstance.Acquire();
            using var explicitName = SingleInstance.Acquire(UniqueName());

            Assert.True(viaEnvironment.IsOwner);
            Assert.True(explicitName.IsOwner);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SingleInstance.EnvName, previous);
        }
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        var guard = SingleInstance.Acquire(UniqueName());

        guard.Dispose();
        guard.Dispose();
    }
}
