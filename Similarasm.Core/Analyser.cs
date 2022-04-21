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
using System.Runtime.Loader;
using System.Xml;

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
    var assemblies = new Dictionary<string, Assembly>();

    // THIS IS THE MAGIC!
    // .NET Core assembly loading is confusing. Things that happen to be in your bin folder don't just suddenly
    // qualify with the assembly loader. If the assembly isn't specifically referenced by your app, you need to
    // tell .NET Core where to get it EVEN IF IT'S IN YOUR BIN FOLDER.
    // https://stackoverflow.com/questions/43918837/net-core-1-1-type-gettype-from-external-assembly-returns-null
    //
    // The documentation says that any .dll in the application base folder should work, but that doesn't seem
    // to be entirely true. You always have to set up additional handlers if you AREN'T referencing the plugin assembly.
    // https://github.com/dotnet/core-setup/blob/master/Documentation/design-docs/corehost.md
    //
    // To verify, try commenting this out and you'll see that the config system can't load the external plugin type.
    AssemblyLoadContext.Default.Resolving += (AssemblyLoadContext context, AssemblyName assembly) =>
    {
      // DISCLAIMER: NO PROMISES THIS IS SECURE. You may or may not want this strategy. It's up to
      // you to determine if allowing any assembly in the directory to be loaded is acceptable. This
      // is for demo purposes only.
      var assyPath = Assembly.GetExecutingAssembly().Location;
      var assyDir = Path.GetDirectoryName(assyPath);

      Console.WriteLine($"Trying to load {assembly.FullName}");
      if (assemblies.ContainsKey(assembly.Name))
      {
        Console.WriteLine("  loaded from cache");
        return assemblies[assembly.Name];
      }

      var missAssyPath = Path.Combine(assyDir, $"{assembly.Name}.dll");
      if (File.Exists(missAssyPath))
      {
        var missAssy = context.LoadFromAssemblyPath(missAssyPath);
        assemblies.Add(assembly.Name, missAssy);
        return missAssy;
      }
      
      // TODO   look in local nuget cache

      return null;
    };

    var solnName = Path.GetFileNameWithoutExtension(Solution.FilePath);
    Console.WriteLine($"{solnName}");

    var projectGraph = Solution.GetProjectDependencyGraph();

    // create dictionary:
    //    [hash-il-method] --> [fq-method-name]
    var methodMap = new Dictionary<string, string>();

    foreach (var projectId in projectGraph.GetTopologicallySortedProjects())
    {
      var proj = Solution.GetProject(projectId);
      var projName = Path.GetFileNameWithoutExtension(proj.FilePath);
      Console.WriteLine($"  {projName}");


      foreach (var mdr in proj.MetadataReferences)
      {
        //Console.WriteLine($"mdr --> {mdr.Display}");
      }

      NugetPackages(proj);


      var projComp = await proj.GetCompilationAsync();
      await using var dll = new MemoryStream();
      await using var pdb = new MemoryStream();
      var result = projComp.Emit(dll, pdb);
      if (!result.Success)
      {
        Console.WriteLine($"    Failed to emit: {projName}");
        continue;
      }

      var assy = Assembly.Load(dll.ToArray(), pdb.ToArray());
      assemblies.Add(projName, assy);

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
    workspace.WorkspaceFailed += (_, args) =>
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

  private void NugetPackages(Project project)
  {
    var csproj = new XmlDocument();
    csproj.Load(project.FilePath);
    var nodes = csproj.SelectNodes("//PackageReference[@Include and @Version]");
    foreach (XmlNode packageReference in nodes)
    {
      var packageName = packageReference.Attributes["Include"].Value;
      var packageVersion = packageReference.Attributes["Version"].Value;
      //Console.WriteLine($"Pkg: {packageName}:{packageVersion}");
    }
  }

  public void Dispose()
  {
    _sha1.Dispose();
  }
}
