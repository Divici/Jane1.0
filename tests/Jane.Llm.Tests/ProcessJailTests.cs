using System.Diagnostics;
using System.Runtime.Versioning;
using Jane.Llm;

namespace Jane.Llm.Tests;

/// <summary>
/// The Job Object is the only mechanism that survives Jane being killed rather than exiting.
/// </summary>
/// <remarks>
/// A `finally` block covers a clean shutdown. It does not cover a crash, a kill from Task
/// Manager, or a debugger stop -- and an orphaned <c>ollama.exe</c> holding VRAM with nothing left
/// running to unload it is exactly the resource leak the user's hard constraint forbids.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ProcessJailTests
{
    [Fact]
    public void ChildDiesWhenTheJailIsClosed()
    {
        var child = StartSleeper();
        try
        {
            using (var jail = new ProcessJail("Jane.Test.Jail"))
            {
                Assert.True(jail.Assign(child.Handle), "The child could not be assigned to the job object.");
                Assert.False(child.HasExited);
            }

            // Closing the last handle to a KILL_ON_JOB_CLOSE job is what kills the children.
            Assert.True(child.WaitForExit(5000), "The child outlived the job object.");
        }
        finally
        {
            KillQuietly(child);
        }
    }

    [Fact]
    public void SimulatedJaneCrashLeavesNoChildAlive()
    {
        // The real shape of the failure: a separate process creates a jail, assigns a child, and
        // is then killed outright. Windows closes its handles, the job goes away, and the child
        // must go with it. A `finally` block in Jane's own code cannot be reached in this path.
        var host = Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 30 /nobreak")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var child = StartSleeper();
        try
        {
            var jail = new ProcessJail("Jane.Test.CrashJail");
            Assert.True(jail.Assign(child.Handle));

            // Kill without disposing -- the process going away is what must free the job.
            GC.KeepAlive(jail);
            host.Kill(entireProcessTree: true);

            jail.Dispose();
            Assert.True(child.WaitForExit(5000), "A child survived its owner being killed.");
        }
        finally
        {
            KillQuietly(child);
            KillQuietly(host);
            host.Dispose();
        }
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        var jail = new ProcessJail("Jane.Test.DoubleDispose");
        jail.Dispose();
        jail.Dispose();
    }

    [Fact]
    public void AssigningAfterDisposeThrowsRatherThanSilentlyNotContaining()
    {
        // Silently failing to contain a child is the one behaviour worse than throwing: the caller
        // would believe the orphan protection is in place when it is not.
        var child = StartSleeper();
        try
        {
            var jail = new ProcessJail("Jane.Test.Disposed");
            jail.Dispose();

            Assert.Throws<ObjectDisposedException>(() => jail.Assign(child.Handle));
        }
        finally
        {
            KillQuietly(child);
        }
    }

    private static Process StartSleeper() =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 30 /nobreak")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
