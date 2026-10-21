namespace Demo.DotnetCore.ConfigService.CentralHost;

// Controllers read bodies through StrictJsonRequestReader instead of model binding, so OpenAPI cannot infer them.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class JsonRequestBodyAttribute : Attribute
{
    public JsonRequestBodyAttribute(Type requestType, string example)
    {
        RequestType = requestType;
        Example = example;
    }

    public Type RequestType { get; }

    public string Example { get; }
}
