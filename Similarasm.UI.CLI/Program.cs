namespace Similarasm.UI.CLI;

using CommandLine;
using Core;

internal static class Program
{
  public static async Task Main(string[] args)
  {
    var result = await Parser.Default.ParseArguments<Options>(args)
      .WithParsedAsync(Run);
    await result.WithNotParsedAsync(HandleParseError);
  }

  private static async Task Run(Options opt)
  {
    Console.WriteLine($"Analysing {opt.SolutionFilePath}");
    var anal = await AnalyserFactory.CreateAnalyser(opt.SolutionFilePath);
    await anal.Analyse();
  }

  private static Task HandleParseError(IEnumerable<Error> errs)
  {
    if (errs.IsVersion())
    {
      Console.WriteLine($"Analyser {Analyser.Version}");
      return Task.CompletedTask;
    }

    if (errs.IsHelp())
    {
      HelpRequest();
      return Task.CompletedTask;
    }

    Console.WriteLine($"Parser Fail");
    return Task.CompletedTask;
  }

  private static void HelpRequest()
  {
    Console.WriteLine($"TODO    HelpRequest");
  }
}
