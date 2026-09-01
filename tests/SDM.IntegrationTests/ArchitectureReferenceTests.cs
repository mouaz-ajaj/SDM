using System.Xml.Linq;

namespace SDM.IntegrationTests;

public sealed class ArchitectureReferenceTests
{

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["src/SDM.Core/SDM.Core.csproj"] = [],
            ["src/SDM.Application/SDM.Application.csproj"] = ["src/SDM.Core/SDM.Core.csproj"],
            ["src/SDM.Infrastructure/SDM.Infrastructure.csproj"] =
                ["src/SDM.Application/SDM.Application.csproj", "src/SDM.Core/SDM.Core.csproj"],
            ["src/SDM.Database/SDM.Database.csproj"] =
                ["src/SDM.Application/SDM.Application.csproj", "src/SDM.Core/SDM.Core.csproj"],
            ["src/SDM.Desktop/SDM.Desktop.csproj"] =
                [
                    "src/SDM.Application/SDM.Application.csproj",
                    "src/SDM.Core/SDM.Core.csproj",
                    "src/SDM.Database/SDM.Database.csproj",
                    "src/SDM.Infrastructure/SDM.Infrastructure.csproj",
                ],
            ["src/SDM.NativeHost/SDM.NativeHost.csproj"] =
                [
                    "src/SDM.Application/SDM.Application.csproj",
                    "src/SDM.Core/SDM.Core.csproj",
                    "src/SDM.Infrastructure/SDM.Infrastructure.csproj",
                ],
        };

    [Fact]
    public void ProductProjects_FollowTheDocumentedDependencyDirection()
    {
        string root = FindRepositoryRoot();

        foreach ((string projectPath, string[] expected) in ExpectedReferences)
        {
            string fullProjectPath = Path.Combine(root, projectPath.Replace('/', Path.DirectorySeparatorChar));
            XDocument project = XDocument.Load(fullProjectPath);
            string projectDirectory = Path.GetDirectoryName(fullProjectPath)!;

            string[] actual = project.Descendants("ProjectReference")
                .Where(reference => !IsBuildOnly(reference))
                .Select(reference => reference.Attribute("Include")?.Value)
                .Where(include => include is not null)
                .Select(include => Path.GetFullPath(Path.Combine(projectDirectory, include!)))
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
            Assert.DoesNotContain("src/SDM.Desktop/SDM.Desktop.csproj", actual);
        }
    }

    [Fact]
    public void CoreAndApplication_DoNotReferenceUiOrDatabasePackages()
    {
        string root = FindRepositoryRoot();

        foreach (string projectPath in new[]
                 {
                     "src/SDM.Core/SDM.Core.csproj",
                     "src/SDM.Application/SDM.Application.csproj",
                 })
        {
            XDocument project = XDocument.Load(Path.Combine(root, projectPath.Replace('/', Path.DirectorySeparatorChar)));
            string[] packages = project.Descendants("PackageReference")
                .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
                .ToArray();

            Assert.DoesNotContain(packages, package => package.StartsWith("Avalonia", StringComparison.Ordinal));
            Assert.DoesNotContain(packages, package => package.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// ReferenceOutputAssembly="false" marks a build-order and packaging instruction
    /// rather than a code dependency — Desktop uses one so the native host is built and
    /// copied beside it. Matched by local name because SDK-style project files carry no
    /// XML namespace, which is why the first attempt at this filter matched nothing.
    /// </summary>
    private static bool IsBuildOnly(XElement reference)
    {
        string? value = reference.Attribute("ReferenceOutputAssembly")?.Value
            ?? reference.Elements()
                .FirstOrDefault(child => child.Name.LocalName == "ReferenceOutputAssembly")
                ?.Value;

        return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SDM.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root containing SDM.sln.");
    }
}
