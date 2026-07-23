using OpenMeow.Lab.Orchestration;
using OpenMeow.Lab.UI;
using OpenMeow.Lab.Verification;

namespace OpenMeow.Lab;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            {
                await SelfTest.RunAsync();
                return 0;
            }

            int port = ReadIntArgument(args, "--port", 17777);
            string modelDirectory = ReadStringArgument(
                args,
                "--models",
                Path.Combine(AppContext.BaseDirectory, "models"));
            var tower = new ControlTower(modelDirectory);

            if (args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
            {
                using var cancellation = CreateConsoleCancellation();
                await new McpStdioServer(tower).RunAsync(cancellation.Token);
                return 0;
            }

            await using var http = new LocalhostHttpServer(tower, port);
            using var stop = CreateConsoleCancellation();
            http.Start(stop.Token);

            if (args.Contains("--headless", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"OpenMeow Lab listening on {http.BaseAddress}");
                Console.WriteLine("Press Ctrl+C to stop.");
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
                return 0;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(tower));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"OpenMeow Lab failed: {ex}");
            return 1;
        }
    }

    private static CancellationTokenSource CreateConsoleCancellation()
    {
        var source = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            source.Cancel();
        };
        return source;
    }

    private static int ReadIntArgument(string[] args, string name, int defaultValue)
    {
        string raw = ReadStringArgument(args, name, defaultValue.ToString());
        if (!int.TryParse(raw, out int result) || result is < 1 or > 65535)
            throw new ArgumentException($"{name} must be a port number between 1 and 65535.");
        return result;
    }

    private static string ReadStringArgument(string[] args, string name, string defaultValue)
    {
        int index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return defaultValue;
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
            throw new ArgumentException($"{name} requires a value.");
        return args[index + 1];
    }
}
