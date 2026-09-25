using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using System.Text.Json;
using System;

namespace Soenneker.Extensions.HttpResponseMessage.Tests;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HttpResponseMessageExtensionTests.SampleDto))]
internal partial class TestJsonContext : JsonSerializerContext
{
    internal static JsonTypeInfo<T> Get<T>() =>
        (JsonTypeInfo<T>)(Default.GetTypeInfo(typeof(T)) ?? throw new NotSupportedException($"No generated JSON metadata for {typeof(T)}."));
}
