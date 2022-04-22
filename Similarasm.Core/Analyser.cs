namespace Similarasm.Core;

using System.Globalization;
using System.Text;
using System.Xml.Linq;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
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

public sealed class Analyser : IDisposable
{
  public static Version Version { get; } = new Version(0, 1);

  private Solution _solution;

  private readonly SHA1 _sha1 = SHA1.Create();
  private readonly Dictionary<string, Assembly> _assemblies = new();
  private string _currentTargetFrameworkMoniker = string.Empty;

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
    AssemblyLoadContext.Default.Resolving += OnResolving;

    _assemblies.Clear();

    var solnName = Path.GetFileNameWithoutExtension(_solution.FilePath);
    Console.WriteLine($"{solnName}");

    var projectGraph = _solution.GetProjectDependencyGraph();

    // create dictionary:
    //    [hash-il-method] --> [fq-method-name]
    var methodMap = new Dictionary<string, string>();

    foreach (var projectId in projectGraph.GetTopologicallySortedProjects())
    {
      var proj = _solution.GetProject(projectId);
      var projName = Path.GetFileNameWithoutExtension(proj.FilePath);
      Console.WriteLine($"  {projName}");

      // TODO   support TargetFrameworks ie multiple
      var xmldoc = XDocument.Load(proj.FilePath);
      _currentTargetFrameworkMoniker = xmldoc.Descendants("TargetFramework").Single().Value;

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
      _assemblies.Add(projName, assy);

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

    _solution = await workspace.OpenSolutionAsync(solnFilePath, progress, cancellationToken);
  }

  private Assembly OnResolving(AssemblyLoadContext context, AssemblyName assembly)
  {
    // DISCLAIMER: NO PROMISES THIS IS SECURE. You may or may not want this strategy. It's up to
    // you to determine if allowing any assembly in the directory to be loaded is acceptable. This
    // is for demo purposes only.
    var assyPath = Assembly.GetExecutingAssembly().Location;
    var assyDir = Path.GetDirectoryName(assyPath);

    if (_assemblies.ContainsKey(assembly.Name))
    {
      return _assemblies[assembly.Name];
    }

    var missAssyPath = Path.Combine(assyDir, $"{assembly.Name}.dll");
    if (File.Exists(missAssyPath))
    {
      var missAssy = context.LoadFromAssemblyPath(missAssyPath);
      _assemblies.Add(assembly.Name, missAssy);
      return missAssy;
    }

    // look in local nuget cache
    var target = assembly.Name.ToLowerInvariant();
    var settings = Settings.LoadDefaultSettings(null);

    // /home/trevorde/.nuget/packages/
    var globPackDir = SettingsUtility.GetGlobalPackagesFolder(settings);
    var nuspecDir = Path.Combine(globPackDir, target);
    if (!Directory.Exists(nuspecDir))
    {
      // no folder for some Microsoft components eg
      //    Microsoft.AspNetCore.Razor.SourceGenerator.Tooling.Internal
      _assemblies.Add(assembly.Name, null);
      return null;
    }

    var nuspecVerDirs = Directory.EnumerateDirectories(nuspecDir);
    var nuspecVerDir = nuspecVerDirs.Single(dir =>
    {
      // can get some directory names which are not strictly versions eg
      //    3.1.0-preview3.19555.2
      if (Version.TryParse(Path.GetFileName(dir), out var nuspecVerVer))
      {
        return
          assembly.Version.Major == nuspecVerVer.Major &&
          assembly.Version.Minor == nuspecVerVer.Minor &&
          assembly.Version.Build == nuspecVerVer.Build;
      }
      return false;
    });
    var nuspecVer = Path.GetFileName(nuspecVerDir);
    var archiveFilePath = Path.Combine(nuspecVerDir, $"{target}.{nuspecVer}{PackagingCoreConstants.NupkgExtension}");
    var isPkg = PackageHelper.IsPackageFile(archiveFilePath, PackageSaveMode.Defaultv3);
    var par = new PackageArchiveReader(archiveFilePath);
    var fwSpecGrps = par.GetLibItems();
    var fwAssyMap = fwSpecGrps.ToDictionary(
      fwspg => GetTargetFrameworkMoniker(fwspg.TargetFramework),
      fwspg => fwspg.Items.Single(item => PackageHelper.IsAssembly(item)));

    // since TFMs are ordered, search backward from latest TFM to get most up to date assy
    // which is compatible with current project target framework
    var currTFMGroup = GetTargetFrameworkMonikerGroup(_currentTargetFrameworkMoniker);
    var currTFMGroupIdx = currTFMGroup.IndexOf(_currentTargetFrameworkMoniker);
    for (var i = currTFMGroupIdx; i >= 0; i--)
    {
      var entry = currTFMGroup[i];
      if (!fwAssyMap.ContainsKey(entry))
      {
        continue;
      }

      var reqAssyPath = Path.Combine(nuspecVerDir, fwAssyMap[entry]);
      var reqAssy = Assembly.LoadFile(reqAssyPath);
      _assemblies.Add(assembly.Name, reqAssy);
      return reqAssy;
    }

    _assemblies.Add(assembly.Name, null);
    return null;
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

  private static string GetTargetFrameworkMoniker(NuGetFramework fwk)
  {
    var framework = GetDisplayVersion(fwk);
    var version = GetDisplayVersion(fwk.Version);
    if (framework == "net")
    {
      version = version.Replace(".", "");
    }

    var retval = $"{framework}{version}";

    // START_HACK
    // Some components (Microsoft.AspNetCore.Components) have their framework as '.NETCoreApp'
    // We map here rather than in GetDisplayVersion so we only map exact matches rather
    // than all frameworks with '.NETCoreApp' eg '.NETCoreApp3.0'
    switch (retval)
    {
      case "netcoreapp5.0":
        retval = "net5.0";
        break;
      case "netcoreapp6.0":
        retval = "net6.0";
        break;
    }
    // END_HACK

    return retval;
  }

  private static string GetDisplayVersion(NuGetFramework fwk)
  {
    // .NETFramework --> net
    var retval = fwk.Framework
      .ToLowerInvariant()
      .Replace(".", "")
      .Replace("netframework", "net");

    return retval;
  }

  private static string GetDisplayVersion(Version version)
  {
    var sb = new StringBuilder(string.Format(CultureInfo.InvariantCulture, "{0}.{1}", version.Major, version.Minor));

    if (version.Build > 0 ||
        version.Revision > 0)
    {
      sb.AppendFormat(CultureInfo.InvariantCulture, ".{0}", version.Build);

      if (version.Revision > 0)
      {
        sb.AppendFormat(CultureInfo.InvariantCulture, ".{0}", version.Revision);
      }
    }

    return sb.ToString();
  }

  /// <summary>
  /// returns an ordered list of Target Framework Monikers,
  /// ordered by release date from earliest to latest
  /// </summary>
  /// <param name="tfm"></param>
  /// <returns>ordered list of Target Framework Monikers</returns>
  /// <exception cref="ArgumentOutOfRangeException"></exception>
  private static IReadOnlyList<string> GetTargetFrameworkMonikerGroup(string tfm)
  {
    // stolen from:
    //    https://docs.microsoft.com/en-us/dotnet/standard/frameworks

    // .NET 5+ (and .NET Core)
    var netCore = new[]
    {
      "netcoreapp1.0",
      "netcoreapp1.1",
      "netcoreapp2.0",
      "netcoreapp2.1",
      "netcoreapp2.2",
      "netcoreapp3.0",
      "netcoreapp3.1",
      "net5.0",
      "net6.0"
    };
    if (netCore.Contains(tfm))
    {
      return netCore.ToList();
    }

    // .NET Standard
    var netStd = new[]
    {
      "netstandard1.0",
      "netstandard1.1",
      "netstandard1.2",
      "netstandard1.3",
      "netstandard1.4",
      "netstandard1.5",
      "netstandard1.6",
      "netstandard2.0",
      "netstandard2.1"
    };
    if (netStd.Contains(tfm))
    {
      return netStd.ToList();
    }

    // .NET Framework
    var netFramework = new[]
    {
      "net11",
      "net20",
      "net35",
      "net40",
      "net403",
      "net45",
      "net451",
      "net452",
      "net46",
      "net461",
      "net462",
      "net47",
      "net471",
      "net472",
      "net48"
    };
    if (netFramework.Contains(tfm))
    {
      return netFramework.ToList();
    }

    throw new ArgumentOutOfRangeException($"Unknown TargetFrameworkMoniker: {tfm}");
  }

  public void Dispose()
  {
    _sha1.Dispose();
  }
}
