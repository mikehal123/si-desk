using Sovereign.Desktop.SetupLib;

namespace Sovereign.Desktop.PreviewSetup;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var parseError = SetupCli.ParseOrError(args, out var request);
            _ = request;
            if (parseError is not null)
            {
                foreach (var line in parseError.Log)
                {
                    Console.WriteLine(line);
                }

                Console.WriteLine(parseError.Message);
                Console.Error.WriteLine(parseError.Message);
                return parseError.ExitCode;
            }

            var result = new SetupResult(1, "The installer engine is not in this copy.", []);
            foreach (var line in result.Log)
            {
                Console.WriteLine(line);
            }

            Console.WriteLine(result.Message);
            if (result.ExitCode != 0)
            {
                Console.Error.WriteLine(result.Message);
            }

            return result.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

}
