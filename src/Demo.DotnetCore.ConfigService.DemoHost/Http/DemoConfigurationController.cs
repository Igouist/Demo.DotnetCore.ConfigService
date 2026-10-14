using Microsoft.AspNetCore.Mvc;

namespace Demo.DotnetCore.ConfigService.DemoHost;

[ApiController]
[Route("api/v1/demo")]
public sealed class DemoConfigurationController(EffectiveConfigurationReader reader) : ControllerBase
{
    private const string UnsafeWarning =
        "This response may expose sensitive configuration values.";

    [HttpGet("effective-configuration")]
    public IActionResult GetEffectiveConfiguration()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new EffectiveConfigurationResponse("Demo", reader.ReadDemoValues()));
    }

    [HttpGet("unsafe/root-configuration")]
    public IActionResult GetUnsafeRootConfiguration()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new UnsafeRootConfigurationResponse(
            "Root",
            UnsafeWarning,
            reader.ReadRootValues()));
    }
}
