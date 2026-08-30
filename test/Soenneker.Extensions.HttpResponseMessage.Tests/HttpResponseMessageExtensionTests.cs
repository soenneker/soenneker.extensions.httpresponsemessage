using System;
using System.Net;
using System.Text;
using System.Threading;
using AwesomeAssertions;
using Soenneker.Tests.Unit;

namespace Soenneker.Extensions.HttpResponseMessage.Tests;

public class HttpResponseMessageExtensionTests : UnitTest
{
    [Test]
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

    [Test]
    public async System.Threading.Tasks.Task ToWithString_ReturnsContentAndNullResponse_ForNonJson()
    {
        const string payload = "not json";
        using var response = new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new System.Net.Http.StringContent(payload, Encoding.UTF8, "text/plain");

        (SampleDto? dto, string? content) = await response.ToWithString<SampleDto>();

        dto.Should().BeNull();
        content.Should().Be(payload);
    }

    [Test]
    public async System.Threading.Tasks.Task ToWithString_ReturnsContentWhenJsonInvalid()
    {
        const string invalidJson = "{\"Name\":\"Test\"";
        using var response = new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new System.Net.Http.StringContent(invalidJson, Encoding.UTF8, "application/json");

        (SampleDto? dto, string? content) = await response.ToWithString<SampleDto>();

        dto.Should().BeNull();
        content.Should().Be(invalidJson);
    }

    [Test]
    public async System.Threading.Tasks.Task ToWithString_ReturnsEmptyString_ForNoContent()
    {
        using var response = new System.Net.Http.HttpResponseMessage(HttpStatusCode.NoContent);
        response.Content = new System.Net.Http.StringContent(string.Empty, Encoding.UTF8, "application/json");

        (SampleDto? dto, string? content) = await response.ToWithString<SampleDto>();

        dto.Should().BeNull();
        content.Should().BeEmpty();
    }

    [Test]
    public async System.Threading.Tasks.Task To_propagates_requested_cancellation()
    {
        using var response = new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("{\"Name\":\"Test\"}", Encoding.UTF8, "application/json")
        };
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.That(async () => await response.To<SampleDto>(cancellationToken: cancellation.Token))
                    .Throws<OperationCanceledException>();
    }

    private sealed class SampleDto
    {
        public string? Name { get; set; }
    }
}
