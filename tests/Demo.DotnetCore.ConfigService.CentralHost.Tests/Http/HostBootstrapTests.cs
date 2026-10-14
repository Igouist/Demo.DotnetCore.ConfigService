using System.Net;
using Dapper;
using Demo.DotnetCore.ConfigService.CentralHost.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Http;

public sealed class HostBootstrapTests
{
    [Fact]
    public void Entry_point_is_publicly_discoverable()
    {
        Assert.True(typeof(Program).IsPublic);
    }

    [Fact]
    public async Task Host_starts_and_leaves_unmatched_routes_outside_the_poc_contract()
    {
        string contentRootPath;
        string databaseDirectoryPath;

        await using (var factory = new CentralHostWebApplicationFactory())
        {
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/not-a-poc-route");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            contentRootPath = factory.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;
            databaseDirectoryPath = Path.GetDirectoryName(factory.DatabasePath)!;
            await AssertInitializedDatabaseAsync(factory.DatabasePath);
        }

        Assert.False(Directory.Exists(databaseDirectoryPath));
        var repositoryRoot = FindRepositoryRoot();
        AssertNoDefaultDatabaseArtifacts(repositoryRoot);
        AssertNoDefaultDatabaseArtifacts(contentRootPath);
    }

    private static async Task AssertInitializedDatabaseAsync(string databasePath)
    {
        Assert.True(File.Exists(databasePath));
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString);
        await connection.OpenAsync();

        Assert.Equal(1, await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));
        Assert.Equal("wal", await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(
                directory.FullName,
                "Demo.DotnetCore.ConfigService.sln")))
        {
            directory = directory.Parent;
        }

        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }

    private static void AssertNoDefaultDatabaseArtifacts(string rootPath)
    {
        foreach (var fileName in new[]
                 {
                     "central-config.db",
                     "central-config.db-wal",
                     "central-config.db-shm"
                 })
        {
            Assert.False(File.Exists(Path.Combine(rootPath, fileName)));
        }
    }
}
