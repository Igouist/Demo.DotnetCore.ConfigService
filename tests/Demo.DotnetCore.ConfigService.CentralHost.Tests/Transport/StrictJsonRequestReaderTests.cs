using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Demo.DotnetCore.ConfigService.CentralHost.Tests.Transport;

public sealed class StrictJsonRequestReaderTests
{
    [Fact]
    public async Task ReadRequiredAsync_rejects_an_absent_body_with_a_safe_contract_error()
    {
        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            CreateReader().ReadRequiredAsync<SetConfigurationRequest>(
                CreateRequest(string.Empty),
                AllowedRequestMembers.Set,
                CancellationToken.None));

        AssertError(exception, "$", "body_required", "A JSON request body is required.");
        AssertDoesNotLeak(exception);
    }

    [Fact]
    public async Task ReadRequiredAsync_rejects_malformed_json_with_a_safe_contract_error()
    {
        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            CreateReader().ReadRequiredAsync<SetConfigurationRequest>(
                CreateRequest("{"),
                AllowedRequestMembers.Set,
                CancellationToken.None));

        AssertError(exception, "$", "invalid_json", "The request body must contain valid JSON.");
        AssertDoesNotLeak(exception);
    }

    [Fact]
    public async Task ReadRequiredAsync_rejects_a_non_object_root_with_a_safe_contract_error()
    {
        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            CreateReader().ReadRequiredAsync<SetConfigurationRequest>(
                CreateRequest("[]"),
                AllowedRequestMembers.Set,
                CancellationToken.None));

        AssertError(exception, "$", "invalid_type", "A request value has an incompatible JSON type.");
        AssertDoesNotLeak(exception);
    }

    [Fact]
    public async Task ReadRequiredAsync_rejects_an_incompatible_property_type_with_the_json_path()
    {
        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            CreateReader().ReadRequiredAsync<SetConfigurationRequest>(
                CreateRequest("{\"environment\":7,\"key\":\"Demo:Message\",\"value\":\"x\"}"),
                AllowedRequestMembers.Set,
                CancellationToken.None));

        AssertError(exception, "$.environment", "invalid_type", "A request value has an incompatible JSON type.");
        AssertDoesNotLeak(exception);
    }

    [Fact]
    public async Task ReadRequiredAsync_rejects_an_unknown_property_without_echoing_its_name()
    {
        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            CreateReader().ReadRequiredAsync<SetConfigurationRequest>(
                CreateRequest("{\"environment\":\"UAT\",\"key\":\"Demo:Message\",\"value\":\"x\",\"extra\":1}"),
                AllowedRequestMembers.Set,
                CancellationToken.None));

        AssertError(exception, "$", "unknown_property", "The request body contains an unknown property.");
        AssertDoesNotLeak(exception);
    }

    [Fact]
    public async Task ReadRequiredAsync_rejects_a_disallowed_known_property_without_echoing_its_value()
    {
        var exception = await Assert.ThrowsAsync<RequestValidationException>(() =>
            CreateReader().ReadRequiredAsync<SetConfigurationRequest>(
                CreateRequest("{\"environment\":\"UAT\",\"key\":\"Demo:Message\",\"value\":\"must-not-echo\"}"),
                AllowedRequestMembers.Remove,
                CancellationToken.None));

        AssertError(exception, "$", "unknown_property", "The request body contains an unknown property.");
        AssertDoesNotLeak(exception);
    }

    [Fact]
    public async Task ReadRequiredAsync_deserializes_a_valid_set_request()
    {
        var result = await CreateReader().ReadRequiredAsync<SetConfigurationRequest>(
            CreateRequest("{\"environment\":\"UAT\",\"key\":\"Demo:Message\",\"value\":\"x\",\"changedBy\":\"alice\"}"),
            AllowedRequestMembers.Set,
            CancellationToken.None);

        Assert.Equal(new SetConfigurationRequest("UAT", "Demo:Message", "x", "alice", null), result);
    }

    private static StrictJsonRequestReader CreateReader() =>
        new(Options.Create(new JsonOptions()));

    private static HttpRequest CreateRequest(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return context.Request;
    }

    private static void AssertError(RequestValidationException exception, string path, string code, string message)
    {
        var error = Assert.Single(exception.Errors);
        Assert.Equal(path, error.Path);
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
    }

    private static void AssertDoesNotLeak(RequestValidationException exception)
    {
        var parserMessage = Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse("{")).Message;
        var exceptionText = exception.ToString();

        Assert.DoesNotContain("extra", exceptionText, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-echo", exceptionText, StringComparison.Ordinal);
        Assert.DoesNotContain(parserMessage, exceptionText, StringComparison.Ordinal);
    }
}
