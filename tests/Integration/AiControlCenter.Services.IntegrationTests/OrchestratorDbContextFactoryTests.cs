using AiControlCenter.Orchestrator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiControlCenter.Services.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OrchestratorDesignTimeEnvironmentDefinition
{
    public const string Name = "Orchestrator design-time environment";
}

[Collection(OrchestratorDesignTimeEnvironmentDefinition.Name)]
public sealed class OrchestratorDbContextFactoryTests
{
    private const string VariableName = "ConnectionStrings__OrchestratorDatabase";

    [Fact]
    public void MissingConnectionStringFailsClosedWithoutSensitiveDetails()
    {
        WithEnvironmentVariable(null, () =>
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => new OrchestratorDbContextFactory().CreateDbContext([]));

            Assert.Equal("ConnectionStrings__OrchestratorDatabase is required.", exception.Message);
            Assert.DoesNotContain("Password", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("postgres", exception.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ExplicitConnectionStringIsUsedVerbatim()
    {
        const string connectionString =
            "Host=example.invalid;Port=6543;Database=explicit;Username=explicit-user;Password=not-a-real-secret";

        WithEnvironmentVariable(connectionString, () =>
        {
            using var db = new OrchestratorDbContextFactory().CreateDbContext([]);
            Assert.Equal(connectionString, db.Database.GetDbConnection().ConnectionString);
        });
    }

    [Fact]
    public void InvalidExplicitValueIsNotReplacedByFallback()
    {
        const string invalidConnectionString = "this-is-not-a-connection-string";

        WithEnvironmentVariable(invalidConnectionString, () =>
        {
            using var db = new OrchestratorDbContextFactory().CreateDbContext([]);
            var exception = Assert.Throws<ArgumentException>(() => db.Database.GetDbConnection());
            Assert.DoesNotContain("localhost", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", exception.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static void WithEnvironmentVariable(string? value, Action assertion)
    {
        var original = Environment.GetEnvironmentVariable(VariableName);
        try
        {
            Environment.SetEnvironmentVariable(VariableName, value);
            assertion();
        }
        finally
        {
            Environment.SetEnvironmentVariable(VariableName, original);
        }
    }
}
