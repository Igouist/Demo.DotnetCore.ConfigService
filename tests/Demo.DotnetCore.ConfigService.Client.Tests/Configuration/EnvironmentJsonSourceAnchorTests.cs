using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Configuration;

public sealed class EnvironmentJsonSourceAnchorTests
{
    [Fact]
    public void Finds_the_only_standard_environment_json_source()
    {
        IConfigurationSource[] sources =
        [
            new JsonConfigurationSource { Path = "appsettings.json" },
            new JsonConfigurationSource { Path = "appsettings.UAT.json" }
        ];

        bool found = EnvironmentJsonSourceAnchor.TryFindIndex(sources, "UAT", out int index);

        Assert.True(found);
        Assert.Equal(1, index);
    }

    [Fact]
    public void Missing_or_duplicate_anchor_is_rejected_instead_of_guessed()
    {
        IConfigurationSource[] missing = [new JsonConfigurationSource { Path = "custom.json" }];
        IConfigurationSource[] duplicate =
        [
            new JsonConfigurationSource { Path = "appsettings.UAT.json" },
            new JsonConfigurationSource { Path = "APPSETTINGS.uat.JSON" }
        ];

        bool missingFound = EnvironmentJsonSourceAnchor.TryFindIndex(missing, "UAT", out int missingIndex);
        bool duplicateFound = EnvironmentJsonSourceAnchor.TryFindIndex(duplicate, "UAT", out int duplicateIndex);

        Assert.False(missingFound);
        Assert.Equal(-1, missingIndex);
        Assert.False(duplicateFound);
        Assert.Equal(-1, duplicateIndex);
    }

    [Fact]
    public void Matches_only_the_exact_environment_filename_case_insensitively()
    {
        IConfigurationSource[] sources =
        [
            new JsonConfigurationSource { Path = "appsettings.Production.json" },
            new JsonConfigurationSource { Path = "APPSETTINGS.uat.JSON" }
        ];

        bool found = EnvironmentJsonSourceAnchor.TryFindIndex(sources, "UAT", out int index);

        Assert.True(found);
        Assert.Equal(1, index);
    }
}
