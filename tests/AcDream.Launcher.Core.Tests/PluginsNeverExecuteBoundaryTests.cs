using System.Runtime.CompilerServices;

namespace AcDream.Launcher.Core.Tests;

/// <summary>Pins the L-300 guarantee that nothing plugin-related loads, reflects over, or starts a
/// downloaded file. A source-text scan needs no project reference to the paths it checks, so it
/// catches the launcher UI files (out of scope for this unit) once they exist too.</summary>
public sealed class PluginsNeverExecuteBoundaryTests
{
    private static readonly string[] ForbiddenApis =
    [
        "Assembly.Load",
        "AssemblyLoadContext",
        "MetadataLoadContext",
        "PEReader",
        "Process.Start",
    ];

    [Fact]
    public void NoPluginSourceFileUsesAnExecutionOrReflectionApi()
    {
        string repositoryRoot = FindRepositoryRoot();
        var candidates = new List<string>();
        string pluginsFolder = Path.Combine(
            repositoryRoot,
            "src",
            "AcDream.Launcher.Core",
            "Plugins");
        candidates.AddRange(Directory.GetFiles(pluginsFolder, "*.cs", SearchOption.TopDirectoryOnly));

        string composition = Path.Combine(
            repositoryRoot,
            "src",
            "AcDream.Launcher",
            "LauncherPluginComposition.cs");
        if (File.Exists(composition))
        {
            candidates.Add(composition);
        }

        string viewModels = Path.Combine(repositoryRoot, "src", "AcDream.Launcher", "ViewModels");
        if (Directory.Exists(viewModels))
        {
            candidates.AddRange(Directory.GetFiles(viewModels, "*Plugin*.cs", SearchOption.TopDirectoryOnly));
        }

        Assert.NotEmpty(candidates);

        var offenders = new List<string>();
        foreach (string path in candidates)
        {
            string text = File.ReadAllText(path);
            foreach (string api in ForbiddenApis)
            {
                if (text.Contains(api, StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(path)} uses '{api}'");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourcePath = "")
    {
        string[] starts =
        {
            Path.GetDirectoryName(sourcePath) ?? string.Empty,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };
        foreach (string start in starts)
        {
            if (string.IsNullOrEmpty(start))
            {
                continue;
            }

            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find AcDream.slnx above the source, working, or output directory.");
    }
}
