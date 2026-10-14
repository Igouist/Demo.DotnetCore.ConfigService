using System.Text;
using System.Text.Json;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Distribution;

public sealed class DistributionResolveResponseParserTests
{
    [Fact]
    public void Valid_partition_accepts_response_casing_and_empty_value()
    {
        KeySelection selection = CreateSelection(["Demo:Required"], ["Demo:Fallback"]);
        ReadOnlyMemory<byte> json = Json(
            """
            {
              "values": { "demo:required": "" },
              "missingKeys": [ "DEMO:FALLBACK" ]
            }
            """);

        bool parsed = DistributionResolveResponseParser.TryParseAndValidate(
            json,
            selection,
            out ValidatedDistributionResponse? response);

        Assert.True(parsed);
        Assert.Equal(string.Empty, response!.Values["DEMO:REQUIRED"]);
        Assert.True(response.IsMissing("Demo:Fallback"));
        Assert.Equal(1, response.MissingKeyCount);
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public void Untrustworthy_successful_response_is_rejected(string json)
    {
        KeySelection selection = CreateSelection(["Demo:Required"], ["Demo:Fallback"]);

        bool parsed = DistributionResolveResponseParser.TryParseAndValidate(
            Json(json),
            selection,
            out ValidatedDistributionResponse? response);

        Assert.False(parsed);
        Assert.Null(response);
    }

    [Fact]
    public void Value_limit_is_inclusive_and_measured_as_utf8()
    {
        KeySelection selection = CreateSelection(["Demo:Required"], []);
        string atLimit = new string('界', 5_461) + "a";
        string overLimit = new string('界', 5_462);

        Assert.True(DistributionResolveResponseParser.TryParseAndValidate(
            Json($$"""{ "values": { "Demo:Required": {{JsonSerializer.Serialize(atLimit)}} }, "missingKeys": [] }"""),
            selection,
            out _));
        Assert.False(DistributionResolveResponseParser.TryParseAndValidate(
            Json($$"""{ "values": { "Demo:Required": {{JsonSerializer.Serialize(overLimit)}} }, "missingKeys": [] }"""),
            selection,
            out _));
    }

    public static TheoryData<string> InvalidResponses => new()
    {
        "not-json",
        "[]",
        "{ \"values\": {}, \"values\": {}, \"missingKeys\": [\"Demo:Required\", \"Demo:Fallback\"] }",
        "{ \"Values\": {}, \"values\": {}, \"missingKeys\": [\"Demo:Required\", \"Demo:Fallback\"] }",
        "{ \"values\": {}, \"missingKeys\": [], \"future\": true }",
        "{ \"values\": { \"Demo:Required\": \"a\", \"demo:required\": \"b\" }, \"missingKeys\": [\"Demo:Fallback\"] }",
        "{ \"values\": { \"Demo:Required\": null }, \"missingKeys\": [\"Demo:Fallback\"] }",
        "{ \"values\": { \"Demo:Required\": 1 }, \"missingKeys\": [\"Demo:Fallback\"] }",
        "{ \"values\": { \"Other:Key\": \"x\" }, \"missingKeys\": [\"Demo:Required\", \"Demo:Fallback\"] }",
        "{ \"values\": {}, \"missingKeys\": [\"Demo:Fallback\"] }",
        "{ \"values\": { \"Demo:Required\": \"x\" }, \"missingKeys\": [\"demo:required\", \"Demo:Fallback\"] }",
        "{ \"values\": {}, \"missingKeys\": [\"Demo:Required\", \"demo:required\", \"Demo:Fallback\"] }",
        "{ \"values\": {}, \"missingKeys\": [\"CentralConfig:Enabled\", \"Demo:Fallback\"] }"
    };

    private static KeySelection CreateSelection(string[] required, string[] fallback)
    {
        Assert.True(KeySelection.TryCreate(required, fallback, out KeySelection? selection));
        return selection!;
    }

    private static ReadOnlyMemory<byte> Json(string json) => Encoding.UTF8.GetBytes(json);
}
