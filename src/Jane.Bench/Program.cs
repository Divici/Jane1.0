using Jane.Bench.Commands;
using Jane.Bench.Fixtures;

namespace Jane.Bench;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var environment = new JaneEnvironment();
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        try
        {
            return command switch
            {
                "doctor" => await new DoctorCommand(environment).RunAsync(cts.Token),
                "bench" => await new BenchCommand(environment).RunAsync(cts.Token),
                "fixtures" => await BuildFixturesAsync(environment, cts.Token),
                _ => Help(),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
    }

    private static async Task<int> BuildFixturesAsync(JaneEnvironment environment, CancellationToken cancellationToken)
    {
        var entries = await new FixtureBuilder(environment.RepoRoot).BuildAsync(force: true, cancellationToken);
        Console.WriteLine($"Built {entries.Count} fixtures in {FixtureCorpus.DirectoryFor(environment.RepoRoot)}");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("Jane bench");
        Console.WriteLine();
        Console.WriteLine("  doctor    Environment and Ollama capability probes -> doctor-report.json");
        Console.WriteLine("  bench     Automated ASR engine selection            -> bench-report.json");
        Console.WriteLine("  fixtures  Rebuild the synthesized fixture corpus");
        Console.WriteLine("  route     Print the GPU governor's routing decision");
        Console.WriteLine("  eval      Accuracy and latency regression gate      -> eval-report.json");
        Console.WriteLine();
        Console.WriteLine("Usage: dotnet run --project src/Jane.Bench -- <command>");
        return 1;
    }
}
