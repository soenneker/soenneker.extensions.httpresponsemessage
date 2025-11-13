using System.Net;
using System.Text;
using AwesomeAssertions;
using Soenneker.Tests.Unit;
using Xunit;

namespace Soenneker.Extensions.HttpResponseMessage.Tests;

public class HttpResponseMessageExtensionTests : UnitTest
{
    [Fact]
    public async System.Threading.Tasks.Task ToWithString_ReturnsResponseAndContent_ForValidJson()
    {
        const string json = "{\"Name\":\"Test\"}";
        using var response = new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");

        (SampleDto? dto, string? content) = await response.ToWithString<SampleDto>();

        dto.Should().NotBeNull();
        dto!.Name.Should().Be("Test");
        content.Should().Be(json);
    }

    [Fact]
    public async System.Threading.Tasks.Task ToWithString_ReturnsContentAndNullResponse_ForNonJson()
    {
        const string payload = "not json";
        using var response = new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new System.Net.Http.StringContent(payload, Encoding.UTF8, "text/plain");

        (SampleDto? dto, string? content) = await response.ToWithString<SampleDto>();

        dto.Should().BeNull();
        content.Should().Be(payload);
    }

    [Fact]
    public async System.Threading.Tasks.Task ToWithString_ReturnsContentWhenJsonInvalid()
    {
        const string invalidJson = "{\"Name\":\"Test\"";
        using var response = new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new System.Net.Http.StringContent(invalidJson, Encoding.UTF8, "application/json");

        (SampleDto? dto, string? content) = await response.ToWithString<SampleDto>();

        dto.Should().BeNull();
        content.Should().Be(invalidJson);
    }

    [Fact]
    public async System.Threading.Tasks.Task ToWithString_ReturnsEmptyString_ForNoContent()
    {
        using var response = new System.Net.Http.HttpResponseMessage(HttpStatusCode.NoContent);
        response.Content = new System.Net.Http.StringContent(string.Empty, Encoding.UTF8, "application/json");

        (SampleDto? dto, string? content) = await response.ToWithString<SampleDto>();

        dto.Should().BeNull();
        content.Should().BeEmpty();
    }

    private sealed class SampleDto
    {
        public string? Name { get; set; }
    }
}
