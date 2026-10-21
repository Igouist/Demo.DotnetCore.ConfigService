using System.Text;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Distribution;

public sealed class CentralProblemReferenceTests
{
    [Theory]
    [InlineData("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01")]
    [InlineData("0HN1ABCDEF:0000000A")]
    public void Read_accepts_snake_case_codes_and_known_trace_id_formats(string traceId)
    {
        CentralProblemReference? problem = Read(
            $$"""{"code":"internal_error","traceId":"{{traceId}}","detail":"ignored"}""");

        Assert.Equal(new CentralProblemReference("internal_error", traceId), problem);
    }

    [Theory]
    [InlineData("""{"code":"Not Snake","traceId":"00-0123456789abcdef0123456789abcdef-0123456789abcdef-01"}""", null, "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01")]
    [InlineData("""{"code":"persistence_failure","traceId":"value=central-secret"}""", "persistence_failure", null)]
    [InlineData("""{"code":"persistence_failure","traceId":"00-0123\n"}""", "persistence_failure", null)]
    [InlineData("""{"code":"persistence_failure\n","traceId":"0HN1ABCDEF:0000000A\n"}""", null, null)]
    [InlineData("""{"code":42,"traceId":true}""", null, null)]
    public void Read_drops_fields_that_do_not_match_their_format(string json, string? code, string? traceId)
    {
        CentralProblemReference? problem = Read(json);

        Assert.Equal(code, problem?.Code);
        Assert.Equal(traceId, problem?.TraceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("""{"detail":"only raw text"}""")]
    public void Read_returns_null_without_any_usable_field(string body) =>
        Assert.Null(Read(body));

    [Fact]
    public void Read_rejects_codes_longer_than_64_characters() =>
        Assert.Null(Read($$"""{"code":"{{new string('a', 65)}}"}""")?.Code);

    private static CentralProblemReference? Read(string json) =>
        CentralProblemReference.Read(Encoding.UTF8.GetBytes(json));
}
