namespace Similarasm.UI.CLI;

using CommandLine;
using Core;

internal static class Program
{
  public static void Main(string[] args)
  {
    var result = Parser.Default.ParseArguments<Options>(args)
      .WithParsed(Run);
    result.WithNotParsed(HandleParseError);
  }

  private static void Run(Options opt)
  {
    // TODO   Run
    Console.WriteLine($"Analysing {opt.SolutionFilePath}");
  }

  private static void HandleParseError(IEnumerable<Error> errs)
  {
    if (errs.IsVersion())
    {
      Console.WriteLine($"Analyser {Analyser.Version}");
      return;
    }

    if (errs.IsHelp())
    {
      HelpRequest();
      return;
    }

    Console.WriteLine($"Parser Fail");
    return;
  }

  private static void HelpRequest()
  {
    Console.WriteLine($"TODO    HelpRequest");
  }
}
