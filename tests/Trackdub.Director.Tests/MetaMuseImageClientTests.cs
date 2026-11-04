using System.Net;
using System.Text;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Tests for <see cref="MetaMuseImageClient"/>: model selection, argument guards, and every
/// failure path of the response parser. No network: the HTTP handler is canned.
/// </summary>
public sealed class MetaMuseImageClientTests : IDisposable
{
    private readonly string? _previousModel = Environment.GetEnvironmentVariable(MetaMuseImageClient.ImageModelEnv);

    private readonly string? _previousKey = Environment.GetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv);

    public MetaMuseImageClientTests()
    {
        Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, "LLM_test-fake-key");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MetaMuseImageClient.ImageModelEnv, _previousModel);
        Environment.SetEnvironmentVariable(MetaAuth.PrimaryApiKeyEnv, _previousKey);
    }

    private sealed class CannedHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<Exception> EditAsync(HttpMessageHandler handler, string instruction = "make it red")
    {
        using var http = new HttpClient(handler);
        using var client = new MetaMuseImageClient(http);

        return await Record.ExceptionAsync(
            () => client.EditAsync(new RasterImage(1, 1, [10, 20, 30]), instruction)) ?? new InvalidOperationException("edit unexpectedly succeeded");
    }

    [Fact]
    public void GetImageModel_defaults_when_the_env_var_is_unset()
    {
        Environment.SetEnvironmentVariable(MetaMuseImageClient.ImageModelEnv, null);

        Assert.Equal(MetaMuseImageClient.DefaultImageModel, MetaMuseImageClient.GetImageModel());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetImageModel_defaults_when_the_env_var_is_blank(string value)
    {
        Environment.SetEnvironmentVariable(MetaMuseImageClient.ImageModelEnv, value);

        Assert.Equal(MetaMuseImageClient.DefaultImageModel, MetaMuseImageClient.GetImageModel());
    }

    [Fact]
    public void GetImageModel_honours_the_env_override()
    {
        Environment.SetEnvironmentVariable(MetaMuseImageClient.ImageModelEnv, "  muse-image-2.0  ");

        Assert.Equal("muse-image-2.0", MetaMuseImageClient.GetImageModel());
    }

    [Fact]
    public async Task EditAsync_rejects_a_null_image()
    {
        using var http = new HttpClient(new CannedHandler(() => Json("{}")));
        using var client = new MetaMuseImageClient(http);

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.EditAsync(null!, "instruction"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EditAsync_rejects_a_blank_instruction(string instruction)
    {
        using var http = new HttpClient(new CannedHandler(() => Json("{}")));
        using var client = new MetaMuseImageClient(http);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.EditAsync(new RasterImage(1, 1, [10, 20, 30]), instruction));
    }

    [Fact]
    public async Task EditAsync_surfaces_a_non_success_status()
    {
        Exception error = await EditAsync(new CannedHandler(() => Json("{\"error\":\"boom\"}", HttpStatusCode.InternalServerError)));

        var apiError = Assert.IsType<MetaApiException>(error);
        Assert.Contains("HTTP 500", apiError.Message);
    }

    [Fact]
    public async Task EditAsync_surfaces_the_provider_error_message()
    {
        Exception error = await EditAsync(new CannedHandler(() => Json("{\"error\":{\"message\":\"quota exceeded\"}}")));

        var apiError = Assert.IsType<MetaApiException>(error);
        Assert.Contains("quota exceeded", apiError.Message);
    }

    [Fact]
    public async Task EditAsync_treats_an_unfinished_response_as_a_failure()
    {
        Exception error = await EditAsync(new CannedHandler(() => Json("{\"status\":\"failed\"}")));

        var apiError = Assert.IsType<MetaApiException>(error);
        Assert.Contains("not completed", apiError.Message);
        Assert.Contains("failed", apiError.Message);
    }

    [Fact]
    public async Task EditAsync_fails_when_the_response_carries_no_image()
    {
        Exception error = await EditAsync(new CannedHandler(() => Json("{\"output\":[]}")));

        var apiError = Assert.IsType<MetaApiException>(error);
        Assert.Contains("no image", apiError.Message);
    }

    [Fact]
    public async Task EditAsync_fails_when_the_image_result_is_not_base64()
    {
        Exception error = await EditAsync(
            new CannedHandler(() => Json("{\"output\":[{\"type\":\"image_generation_call\",\"result\":\"not-base64!!\"}]}")));

        var apiError = Assert.IsType<MetaApiException>(error);
        Assert.Contains("not valid base64", apiError.Message);
    }

    [Fact]
    public async Task EditAsync_fails_when_the_body_is_not_json()
    {
        Exception error = await EditAsync(new CannedHandler(() => Json("not json at all")));

        var apiError = Assert.IsType<MetaApiException>(error);
        Assert.Contains("Failed to parse", apiError.Message);
    }

    [Fact]
    public async Task EditAsync_fails_when_the_body_is_not_an_object()
    {
        Exception error = await EditAsync(new CannedHandler(() => Json("[1,2,3]")));

        var apiError = Assert.IsType<MetaApiException>(error);
        Assert.Contains("Unexpected", apiError.Message);
    }

    [Fact]
    public async Task EditAsync_wraps_a_transport_failure()
    {
        Exception error = await EditAsync(new ThrowingHandler());

        var apiError = Assert.IsType<MetaApiException>(error);
        Assert.Contains("request failed", apiError.Message);
        Assert.IsType<HttpRequestException>(apiError.InnerException);
    }
}
