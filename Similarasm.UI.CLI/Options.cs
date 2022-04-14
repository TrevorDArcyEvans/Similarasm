namespace Similarasm.UI.CLI;

using CommandLine;

internal sealed class Options
{
  [Value(index: 0, Required = true, HelpText = "Path to solution file")]
  public string SolutionFilePath { get; set; }
}
