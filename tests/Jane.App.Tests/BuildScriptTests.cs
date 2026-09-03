using System.Diagnostics;
using System.Text;

namespace Jane.App.Tests;

/// <summary>
/// Parses every build script the way Windows PowerShell 5.1 will.
/// </summary>
/// <remarks>
/// These scripts are the one part of Jane that is never compiled, and they have now produced three
/// bugs that only appeared when a person ran them: a Client-Authentication OID where Code Signing
/// was meant, a private key left in the store when signing threw, and an expression split across
/// a line break in a way 5.1 does not accept.
/// <para>
/// That last one is the reason this file exists. Windows PowerShell 5.1 does not continue an
/// expression onto a line that <em>begins</em> with <c>.</c> -- it ends the statement and parses
/// <c>.IsInRole(...)</c> as a command name instead. PowerShell 7 accepts it, so the mistake is
/// invisible to anyone testing in a modern shell, and 5.1 is what ships in the box.
/// </para>
/// <para>
/// The two tests here catch different things, verified by reintroducing the bug and watching which
/// one fired. <see cref="ScriptParsesUnderWindowsPowerShell"/> did <em>not</em> catch it: a line
/// beginning with <c>.IsInRole(...)</c> is perfectly valid syntax -- it is a command invocation --
/// so the parser is happy and the failure only arrives at runtime as "the term '.IsInRole' is not
/// recognized". Only <see cref="NoExpressionIsContinuedOntoALineStartingWithAMemberAccess"/>
/// catches it, which is why a shape check earns its place next to a parse check rather than being
/// redundant with it.
/// </para>
/// </remarks>
public sealed class BuildScriptTests
{
    [Theory]
    [InlineData("get-ollama.ps1")]
    [InlineData("publish.ps1")]
    [InlineData("sign-uiaccess.ps1")]
    [InlineData("install.ps1")]
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

    private static (int ExitCode, string Output) RunPowerShell(string script)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
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

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);
            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty);
        }
    }
}
