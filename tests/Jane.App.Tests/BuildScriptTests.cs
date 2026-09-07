using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Jane.App.Tests;

/// <summary>
/// Exercises every build script the way Windows PowerShell 5.1 will.
/// </summary>
/// <remarks>
/// <para>
/// These scripts are the one part of Jane that is never compiled, and they have now produced three
/// bugs that only appeared when a person ran them: a Client-Authentication OID where Code Signing
/// was meant, a private key left in the store when signing threw, and an expression split across
/// a line break in a way 5.1 does not accept.
/// </para>
/// <para>
/// That last one is the reason this file exists. Windows PowerShell 5.1 does not continue an
/// expression onto a line that <em>begins</em> with <c>.</c> -- it ends the statement and parses
/// <c>.IsInRole(...)</c> as a command name instead. PowerShell 7 accepts it, so the mistake is
/// invisible to anyone testing in a modern shell, and 5.1 is what ships in the box.
/// </para>
/// <para>
/// The first two tests catch different things, verified by reintroducing the bug and watching
/// which one fired. <see cref="ScriptParsesUnderWindowsPowerShell"/> did <em>not</em> catch it: a
/// line beginning with <c>.IsInRole(...)</c> is perfectly valid syntax -- it is a command
/// invocation -- so the parser is happy and the failure only arrives at runtime as "the term
/// '.IsInRole' is not recognized". Only
/// <see cref="NoExpressionIsContinuedOntoALineStartingWithAMemberAccess"/> catches it, which is
/// why a shape check earns its place next to a parse check rather than being redundant with it.
/// </para>
/// <para>
/// The signing test goes further and actually runs the script, because a script whose every
/// interesting failure has been a runtime one is a script that static checks will keep missing.
/// It is the slowest test in the project by a wide margin, and it is worth it.
/// </para>
/// </remarks>
public sealed partial class BuildScriptTests
{
    /// <summary>Generous: generating a 3072-bit key and signing a binary are both real work.</summary>
    private const int ScriptTimeoutMs = 120_000;

    [Theory]
    [InlineData("get-ollama.ps1")]
    [InlineData("publish.ps1")]
    [InlineData("sign-uiaccess.ps1")]
    [InlineData("install.ps1")]
    [InlineData("ship.ps1")]
    public void ScriptParsesUnderWindowsPowerShell(string script)
    {
        var path = PackagingTests.FindRepoFile(Path.Combine("build", script));

        var (exitCode, output) = RunPowerShell($$"""
            $errors = $null
            $null = [System.Management.Automation.Language.Parser]::ParseFile('{{path}}', [ref]$null, [ref]$errors)
            if ($errors -and $errors.Count) {
                $errors | ForEach-Object { Write-Output ("line " + $_.Extent.StartLineNumber + ": " + $_.Message) }
                exit 1
            }
            exit 0
            """);

        Assert.SkipWhen(exitCode == -1, "Windows PowerShell is not available on this machine.");
        Assert.True(exitCode == 0, $"{script} does not parse:\n{output}");
    }

    [Theory]
    [InlineData("sign-uiaccess.ps1")]
    [InlineData("install.ps1")]
    [InlineData("ship.ps1")]
    public void NoExpressionIsContinuedOntoALineStartingWithAMemberAccess(string script)
    {
        // Belt and braces alongside the parse check: the parser catches this particular shape, but
        // stating the rule explicitly means a reader of the scripts learns it rather than
        // rediscovering it from a confusing error.
        var lines = File.ReadAllLines(PackagingTests.FindRepoFile(Path.Combine("build", script)));
        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();

            // Comment-based help lives inside <# #> and its keywords legitimately start with a
            // dot -- .SYNOPSIS, .DESCRIPTION, .EXAMPLE. Skipping the block is what keeps this
            // check about code.
            if (trimmed.StartsWith("<#", StringComparison.Ordinal))
            {
                inBlockComment = true;
            }

            if (inBlockComment)
            {
                if (trimmed.Contains("#>", StringComparison.Ordinal))
                {
                    inBlockComment = false;
                }

                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            // "./build/foo.ps1" and ".\build\foo.ps1" are paths, not member access.
            Assert.False(
                trimmed.StartsWith('.')
                    && !trimmed.StartsWith("./", StringComparison.Ordinal)
                    && !trimmed.StartsWith(".\\", StringComparison.Ordinal),
                $"{script} line {i + 1} begins with a member access: Windows PowerShell 5.1 ends the " +
                $"previous statement at the line break and parses this as a command name.\n  {lines[i]}");
        }
    }

    [Fact]
    public void SigningCanReuseAnExistingKeyRatherThanTrustingASecondRoot()
    {
        // Every update has to be re-signed, because the signature covers the binary. Without a
        // reuse path, sign-uiaccess.ps1 generates a fresh certificate every time and asks the
        // machine to trust it -- so the trust store accumulates one self-signed root per update,
        // each of them a key that can sign anything. Reusing the exported key means exactly one
        // root exists no matter how many times Jane is updated.
        //
        // Driven end to end rather than by reading the script, because the interesting failures
        // in this file have all been runtime ones: an EKU that Set-AuthenticodeSignature refused,
        // a private key left in the store, an expression 5.1 would not parse.
        var script = PackagingTests.FindRepoFile(Path.Combine("build", "sign-uiaccess.ps1"));
        var workspace = Path.Combine(Path.GetTempPath(), "jane-signing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            // Something with a valid PE structure to sign. Jane's own test host will do.
            var binary = Path.Combine(workspace, "Subject.exe");
            File.Copy(Environment.ProcessPath!, binary);
            var pfx = Path.Combine(workspace, "key.pfx");

            var (firstExit, firstOutput) = RunPowerShell($$"""
                $ErrorActionPreference = 'Stop'
                $password = ConvertTo-SecureString 'test-only-not-a-secret' -AsPlainText -Force
                & '{{script}}' -Path '{{binary}}' -PfxPath '{{pfx}}' -PfxPassword $password -SkipTrustStore
                exit 0
                """);

            Assert.SkipWhen(firstExit == -1, "Windows PowerShell is not available on this machine.");
            Assert.True(firstExit == 0, $"generating a key failed:\n{firstOutput}");
            Assert.True(File.Exists(pfx), $"no key was exported:\n{firstOutput}");

            var thumbprints = Thumbprints(firstOutput);

            var (reuseExit, reuseOutput) = RunPowerShell($$"""
                $ErrorActionPreference = 'Stop'
                $password = ConvertTo-SecureString 'test-only-not-a-secret' -AsPlainText -Force
                & '{{script}}' -Path '{{binary}}' -PfxPath '{{pfx}}' -PfxPassword $password -ReuseKey -SkipTrustStore
                exit 0
                """);

            Assert.True(reuseExit == 0, $"reusing the key failed:\n{reuseOutput}");

            // The same certificate, so the root already in the machine's store still validates it.
            Assert.Equal(thumbprints, Thumbprints(reuseOutput));

            // And it did not quietly mint a replacement on the way.
            Assert.DoesNotContain("Generating a code-signing certificate", reuseOutput);
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a passing test over.
            }
        }
    }

    /// <summary>
    /// The certificate thumbprints named in a script's output.
    /// </summary>
    /// <remarks>
    /// Matched by shape rather than by finding the line that says "thumbprint". When a native
    /// executable's stderr is redirected, PowerShell serialises its host output as CLIXML -- one
    /// enormous line carrying every message with its own markup around it, in which "the line
    /// containing the word thumbprint" is the whole transcript and differs between two runs that
    /// produced the same certificate.
    /// </remarks>
    private static string Thumbprints(string output) =>
        string.Join(",", Thumbprint().Matches(output).Select(m => m.Value).Distinct().Order());

    /// <summary>A SHA-1 thumbprint as PowerShell prints one: 40 upper-case hex digits.</summary>
    [GeneratedRegex(@"\b[0-9A-F]{40}\b")]
    private static partial Regex Thumbprint();

    /// <summary>
    /// Runs a script under Windows PowerShell and returns what it said.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stream handling is deliberate rather than incidental. Reading stdout to the end and
    /// only then waiting with a timeout puts the timeout after the part that blocks: a child that
    /// never exits hangs on the read, forever, with the timeout never reached. That is not
    /// hypothetical -- it hung this test project's whole run.
    /// </para>
    /// <para>
    /// Reading the two streams one after the other has the same shape of problem for a different
    /// reason: a child that fills the stderr pipe while the parent is blocked on stdout deadlocks,
    /// because neither side can proceed until the other does. Both are read concurrently here, and
    /// the timeout is enforced by killing the process tree.
    /// </para>
    /// <para>
    /// Stdin is redirected and closed immediately so that anything which tries to prompt gets an
    /// end of file rather than a wait that no one is ever going to satisfy.
    /// </para>
    /// </remarks>
    private static (int ExitCode, string Output) RunPowerShell(string script)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            process.StandardInput.Close();

            try
            {
                // Generating a 3072-bit RSA key is seconds of solid CPU, and `dotnet test` runs
                // every project at once -- including Jane.Speech.Tests, which asserts a wall-clock
                // budget for loading the speech model. Two unrelated test projects competing for
                // cores is how a real measurement turns into a flaky one, so this one yields.
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already exited, or not permitted. Neither is worth failing over.
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(ScriptTimeoutMs))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);

                return (-2, $"powershell.exe did not finish within {ScriptTimeoutMs} ms and was killed.");
            }

            // Only after the process has exited, so neither read can outlive it.
            var output = string.Concat(stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty);
        }
    }

    [Fact]
    public void ShipRunsPublishSignAndInstallInThatOrder()
    {
        // The update path is three commands that always run together, and the one people get
        // wrong is the middle one: forgetting -ReuseKey generates a fresh self-signed root and
        // asks the machine to trust one more key that can sign anything at all.
        var ship = File.ReadAllLines(PackagingTests.FindRepoFile(Path.Combine("build", "ship.ps1")));

        // Only the lines that actually run something. Searching the whole file finds
        // sign-uiaccess.ps1 in the -PfxPath documentation, seventy lines above the first line of
        // code, and concludes that signing happens before publishing.
        var invoked = ship
            .Where(line => line.TrimStart().StartsWith("& (Join-Path", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();

        Assert.Collection(
            invoked,
            publish => Assert.Contains("publish.ps1", publish),
            sign => Assert.Contains("sign-uiaccess.ps1", sign),
            install => Assert.Contains("install.ps1", install));

        // -ReuseKey has to be on the signing call itself, not merely mentioned somewhere in the
        // file: the whole risk is invoking sign-uiaccess.ps1 without it and minting another root.
        Assert.Contains("-ReuseKey", invoked[1]);

        Assert.Contains(ship, line => line.Contains("JANE_SIGNING_PFX", StringComparison.Ordinal));
    }
}
