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

public sealed class Analyser : IDisposable
{
  public static Version Version { get; } = new Version(0, 1);

  public Solution Solution { get; private set; }

  private readonly SHA1 _sha1 = SHA1.Create();

  public static async Task<Analyser> Create(
    string solnFilePath,
    IProgress<ProjectLoadProgress> progress = null,
    CancellationToken cancellationToken = default)
  {
    if (!File.Exists(solnFilePath))
    {
      throw new FileNotFoundException($"Could not find {solnFilePath}");
    }

    var retval = new Analyser();
    await retval.LoadSolution(solnFilePath, progress, cancellationToken);

    return retval;
  }

  public async Task Analyse()
  {
    var solnName = Path.GetFileNameWithoutExtension(Solution.FilePath);
    Console.WriteLine($"{solnName}");

    ProjectDependencyGraph projectGraph = Solution.GetProjectDependencyGraph();
    Dictionary<string, Stream> assemblies = new Dictionary<string, Stream>();

    // create dictionary:
    //    [hash-il-method] --> [fq-method-name]
    var methodMap = new Dictionary<string, string>();

    foreach (var projectId in projectGraph.GetTopologicallySortedProjects())
    {
      var proj = Solution.GetProject(projectId);
      var projName = Path.GetFileNameWithoutExtension(proj.FilePath);
      Console.WriteLine($"  {projName}");

      var projComp = await proj.GetCompilationAsync();
      using var dll = new MemoryStream();
      using var pdb = new MemoryStream();
      var result = projComp.Emit(dll, pdb);
      if (!result.Success)
      {
        Console.WriteLine($"    Failed to emit: {projName}");
        continue;
      }

      var assy = Assembly.Load(dll.ToArray(), pdb.ToArray());

      const BindingFlags flags =
        BindingFlags.Default |
        BindingFlags.IgnoreCase |
        BindingFlags.DeclaredOnly |
        BindingFlags.Instance |
        BindingFlags.Static |
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.FlattenHierarchy;
      try
      {
        foreach (var type in assy.GetTypes())
        {
          Console.WriteLine($"    {type.Name}");

          foreach (var ci in type.GetConstructors(flags))
          {
            ProcessMethod(proj, ci, methodMap);
          }

          foreach (var mi in type.GetMethods(flags))
          {
            ProcessMethod(proj, mi, methodMap);
          }
        }
      }
      catch (Exception e)
      {
        Console.WriteLine($"    {e.Message}");
      }
    }
  }

  private Analyser()
  {
    if (!MSBuildLocator.IsRegistered)
    {
      var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
      MSBuildLocator.RegisterInstance(instances.OrderByDescending(x => x.Version).First());
    }
  }

  private async Task LoadSolution(
    string solnFilePath,
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

    Solution = await workspace.OpenSolutionAsync(solnFilePath, progress, cancellationToken);
  }

  private void ProcessMethod(Project proj, MethodBase mb, Dictionary<string, string> methodMap)
  {
    var il = mb.GetMethodBody()?.GetILAsByteArray();
    if (il is null)
    {
      return;
    }

    var hash = string.Concat(_sha1.ComputeHash(il).Select(x => x.ToString("X2")));
    var projName = Path.GetFileNameWithoutExtension(proj.FilePath);
    var fullName = $"{projName}:{mb.DeclaringType.FullName}.{mb.Name}";
    if (!methodMap.ContainsKey(hash))
    {
      Console.WriteLine($"      {fullName}");
      methodMap.Add(hash, fullName);
    }
    else
    {
      Console.WriteLine($"      ***{fullName} <--> {methodMap[hash]}");
    }
  }

  public void Dispose()
  {
    _sha1.Dispose();
  }
}
