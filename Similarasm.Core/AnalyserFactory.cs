namespace Similarasm.Core;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.MSBuild;

public static class AnalyserFactory
{
  public static async Task<Analyser> CreateAnalyser(
    string solnFilePath,
    IProgress<ProjectLoadProgress> progress = null,
    CancellationToken cancellationToken = default)
  {
    return await Analyser.Create(solnFilePath, progress, cancellationToken);
  }
}
