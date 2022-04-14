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

    foreach (ProjectId projectId in projectGraph.GetTopologicallySortedProjects())
    {
      var projectCompilation = await Solution.GetProject(projectId).GetCompilationAsync();
      using var dll = new MemoryStream();
      using var pdb = new MemoryStream();
      var result = projectCompilation.Emit(dll, pdb);
      var assy = Assembly.Load(dll.ToArray(), pdb.ToArray());

      var projPath = Solution.GetProject(projectId);
      var projName = Path.GetFileNameWithoutExtension(projPath.FilePath);
      Console.WriteLine($"  {projName}");

      const BindingFlags flags =
        BindingFlags.Default |
        BindingFlags.IgnoreCase |
        BindingFlags.DeclaredOnly |
        BindingFlags.Instance |
        BindingFlags.Static |
        BindingFlags.Public |
        BindingFlags.NonPublic |
        BindingFlags.FlattenHierarchy;
      using var sha1 = SHA1.Create();
      foreach (var type in assy.GetTypes())
      {
        Console.WriteLine($"    {type.Name}");

        foreach(var ci in type.GetConstructors(flags))
        {
          var il = ci.GetMethodBody()?.GetILAsByteArray();
          var hash = string.Concat(sha1.ComputeHash(il).Select(x => x.ToString("X2")));
          var fullName = ci.DeclaringType.FullName + "." + ci.Name;
          if (!methodMap.ContainsKey(hash))
          {
            Console.WriteLine($"      {fullName}");
            methodMap.Add(hash, fullName);
          }
          else
          {
            Console.WriteLine($"      ***{fullName}");
          }
        }

        foreach (var mi in type.GetMethods(flags))
        {
          var il = mi.GetMethodBody()?.GetILAsByteArray();
          if (il is null)
          {
            continue;
          }

          var hash = string.Concat(sha1.ComputeHash(il).Select(x => x.ToString("X2")));
          var fullName = mi.DeclaringType.FullName + "." + mi.Name;
          if (!methodMap.ContainsKey(hash))
          {
            Console.WriteLine($"      {fullName}");
            methodMap.Add(hash, fullName);
          }
          else
          {
            Console.WriteLine($"      ***{fullName}");
          }
        }
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
}
