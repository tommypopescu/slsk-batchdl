using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sldl.Api;

public static class SldlApiJson
{
    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        ConfigureSerializerOptions(options);
        return options;
    }

    public static void ConfigureSerializerOptions(JsonSerializerOptions options)
    {
        if (!options.TypeInfoResolverChain.Contains(SldlApiJsonContext.Default))
            options.TypeInfoResolverChain.Insert(0, SldlApiJsonContext.Default);
    }
}
