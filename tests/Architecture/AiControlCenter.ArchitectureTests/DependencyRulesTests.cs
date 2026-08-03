using System.Xml.Linq;

namespace AiControlCenter.ArchitectureTests;

public sealed class DependencyRulesTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ServicesRoot = Path.Combine(RepositoryRoot, "src", "Services");

    [Fact]
    public void ProductionProjectsFollowLayerDependencyRules()
    {
        var projects = Directory.GetFiles(
            Path.Combine(RepositoryRoot, "src"),
            "*.csproj",
            SearchOption.AllDirectories);

        foreach (var project in projects)
        {
            var references = GetProjectReferences(project);
            var fileName = Path.GetFileNameWithoutExtension(project);

            if (fileName.EndsWith(".Domain", StringComparison.Ordinal))
            {
                Assert.Empty(references);
                AssertDomainPackagesAreClean(project);
            }
            else if (fileName.EndsWith(".Application", StringComparison.Ordinal))
            {
                Assert.All(references, reference => Assert.EndsWith(".Domain.csproj", reference));
            }
            else if (fileName.EndsWith(".Infrastructure", StringComparison.Ordinal))
            {
                Assert.All(references, reference =>
                    Assert.True(
                        reference.EndsWith(".Application.csproj", StringComparison.Ordinal)
                        || reference.EndsWith(".Domain.csproj", StringComparison.Ordinal),
                        $"Infrastructure reference is not allowed: {reference}"));
            }

            AssertNoCrossServiceReference(project, references);
        }
    }

    [Fact]
    public void GatewayAndWorkerReferenceOnlyBuildingBlocks()
    {
        var hosts = new[]
        {
            Path.Combine(RepositoryRoot, "src", "Gateway", "AiControlCenter.Gateway", "AiControlCenter.Gateway.csproj"),
            Path.Combine(RepositoryRoot, "src", "Services", "Worker", "AiControlCenter.Worker.Service", "AiControlCenter.Worker.Service.csproj"),
        };

        foreach (var host in hosts)
        {
            Assert.All(GetProjectReferences(host), reference =>
                Assert.Contains(
                    $"{Path.DirectorySeparatorChar}BuildingBlocks{Path.DirectorySeparatorChar}",
                    reference,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AssertDomainPackagesAreClean(string project)
    {
        var bannedPrefixes = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore",
            "MassTransit",
            "Npgsql",
            "Grpc",
            "OpenTelemetry",
        };

        var packages = GetPackageReferences(project);
        Assert.DoesNotContain(packages, package =>
            bannedPrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private static void AssertNoCrossServiceReference(string project, IReadOnlyCollection<string> references)
    {
        var relative = Path.GetRelativePath(ServicesRoot, project);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return;
        }

        var owner = relative.Split(Path.DirectorySeparatorChar)[0];
        foreach (var reference in references.Where(reference => reference.StartsWith(ServicesRoot, StringComparison.OrdinalIgnoreCase)))
        {
            var referencedOwner = Path.GetRelativePath(ServicesRoot, reference)
                .Split(Path.DirectorySeparatorChar)[0];
            Assert.Equal(owner, referencedOwner);
        }
    }

    private static string[] GetProjectReferences(string project)
    {
        var directory = Path.GetDirectoryName(project)!;
        return XDocument.Load(project)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(Path.Combine(directory, value!)))
            .ToArray();
    }

    private static string[] GetPackageReferences(string project) =>
        XDocument.Load(project)
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiControlCenter.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate AiControlCenter.sln.");
    }
}
