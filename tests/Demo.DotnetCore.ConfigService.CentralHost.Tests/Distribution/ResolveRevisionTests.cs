using System.Text;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Distribution;

public sealed class ResolveRevisionTests
{
    private static readonly EnvironmentName Uat = EnvironmentName.FromValidated("UAT");

    [Fact]
    public void Canonical_input_folds_case_sorts_keys_and_records_versions()
    {
        byte[] canonical = ResolveRevision.CanonicalUtf8(
            EnvironmentName.FromValidated("uat"),
            [new("Demo:Message", 3), new("demo:LocalFallback", 0)]);

        Assert.Equal(
            """["UAT",["DEMO:LOCALFALLBACK",0],["DEMO:MESSAGE",3]]""",
            Encoding.UTF8.GetString(canonical));
    }

    [Fact]
    public void Matches_the_worked_example_in_the_knowledge_note()
    {
        Assert.Equal(
            "r1-8bbca0abc2ea8e798b908301",
            ResolveRevision.Compute(Uat, [new("Demo:Message", 3), new("Demo:LocalFallback", 0)]));
    }

    [Fact]
    public void Revision_is_prefixed_short_lowercase_hex_and_stable()
    {
        string revision = ResolveRevision.Compute(Uat, [new("Demo:Message", 3)]);

        Assert.Matches("^r1-[0-9a-f]{24}$", revision);
        Assert.Equal(revision, ResolveRevision.Compute(Uat, [new("Demo:Message", 3)]));
    }

    [Fact]
    public void Spelling_and_request_order_do_not_change_the_revision()
    {
        string revision = ResolveRevision.Compute(Uat, [new("Demo:Message", 3), new("Demo:Other", 0)]);

        Assert.Equal(
            revision,
            ResolveRevision.Compute(
                EnvironmentName.FromValidated("uat"),
                [new("demo:other", 0), new("DEMO:MESSAGE", 3)]));
    }

    [Fact]
    public void Any_version_environment_or_key_set_change_changes_the_revision()
    {
        string revision = ResolveRevision.Compute(Uat, [new("Demo:Message", 3), new("Demo:Other", 0)]);

        Assert.NotEqual(revision, ResolveRevision.Compute(Uat, [new("Demo:Message", 4), new("Demo:Other", 0)]));
        Assert.NotEqual(revision, ResolveRevision.Compute(Uat, [new("Demo:Message", 3), new("Demo:Other", 1)]));
        Assert.NotEqual(revision, ResolveRevision.Compute(Uat, [new("Demo:Message", 3)]));
        Assert.NotEqual(
            revision,
            ResolveRevision.Compute(
                EnvironmentName.FromValidated("Production"),
                [new("Demo:Message", 3), new("Demo:Other", 0)]));
    }

    [Fact]
    public void Keys_containing_separators_stay_unambiguous()
    {
        Assert.NotEqual(
            ResolveRevision.Compute(Uat, [new("A:B\",[\"C", 1)]),
            ResolveRevision.Compute(Uat, [new("A:B", 1), new("C", 1)]));
    }
}
