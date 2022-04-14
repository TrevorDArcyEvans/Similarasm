namespace Similarasm.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Security.Cryptography;

public sealed class Analyser
{
  public static Version Version { get; } = new Version(0, 1);

  public Solution Solution { get; private set; }

  private readonly string _solnFilePath;

  public static async Task<Analyser> Create(
    string solnFilePath,
    IProgress<ProjectLoadProgress> progress = null,
    CancellationToken cancellationToken = default)
  {
    if (!File.Exists(solnFilePath))
    {
      throw new FileNotFoundException($"Could not find {solnFilePath}");
    }

    var retval = new Analyser(solnFilePath);
    await retval.LoadSolution(progress, cancellationToken);

    return retval;
  }

  public async Task Analyse()
  {
    // TODO   Analyse
    ProjectDependencyGraph projectGraph = Solution.GetProjectDependencyGraph();
    Dictionary<string, Stream> assemblies = new Dictionary<string, Stream>();

    foreach (ProjectId projectId in projectGraph.GetTopologicallySortedProjects())
    {
      var projectCompilation = await Solution.GetProject(projectId).GetCompilationAsync();
      using var dll = new MemoryStream();
      using var pdb = new MemoryStream();
      var result = projectCompilation.Emit(dll, pdb);
      var assy = Assembly.Load(dll.ToArray(), pdb.ToArray());

      // TODO   create dictionary:
      //    [hash-il-method] --> [fq-method-name]
      var methodMap = new Dictionary<string, string>();

      using var sha1 = SHA1.Create();
      foreach (var type in assy.GetTypes())
      {
        foreach (var mi in type.GetMethods())
        {
          var il = mi.GetMethodBody()?.GetILAsByteArray();
          var hash = string.Concat(sha1.ComputeHash(il).Select(x => x.ToString("X2")));
          var name = mi.GetType().AssemblyQualifiedName;
          methodMap.Add(hash, name);
        }
      }
    }
  }

  private Analyser(string solnFilePath)
  {
    if (!MSBuildLocator.IsRegistered)
    {
      var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
      MSBuildLocator.RegisterInstance(instances.OrderByDescending(x => x.Version).First());
    }

    _solnFilePath = solnFilePath;
  }

  private async Task LoadSolution(
    IProgress<ProjectLoadProgress> progress = null,
    CancellationToken cancellationToken = default)
  {
    var workspace = MSBuildWorkspace.Create();
    workspace.SkipUnrecognizedProjects = true;
    workspace.WorkspaceFailed += (sender, args) =>
    {
      if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
      {
        Console.Error.WriteLine(args.Diagnostic.Message);
      }
    };

    Solution = await workspace.OpenSolutionAsync(_solnFilePath, progress, cancellationToken);
  }
}
