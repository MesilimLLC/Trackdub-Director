using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

/// <summary>
/// Tests for <see cref="TtsApiException"/>, in particular the auth-failure classifier
/// that decides whether a rejected or missing key fails the whole request.
/// </summary>
public sealed class TtsApiExceptionTests
{
    [Theory]
    [InlineData("ElevenLabs API HTTP 401: unauthorized")]
    [InlineData("ElevenLabs API HTTP 403: forbidden")]
    [InlineData("No ElevenLabs API key configured.")]
    public void IsAuthFailure_is_true_for_auth_messages(string message)
    {
        Assert.True(new TtsApiException(message).IsAuthFailure);
    }

    [Theory]
    [InlineData("ElevenLabs API HTTP 500: boom")]
    [InlineData("ElevenLabs API request timed out.")]
    [InlineData("ElevenLabs API returned no audio.")]
    public void IsAuthFailure_is_false_for_other_failures(string message)
    {
        Assert.False(new TtsApiException(message).IsAuthFailure);
    }

    [Fact]
    public void Constructor_preserves_the_message_and_inner_exception()
    {
        var inner = new InvalidOperationException("root cause");
        var exception = new TtsApiException("ElevenLabs API request failed.", inner);

        Assert.Equal("ElevenLabs API request failed.", exception.Message);
        Assert.Same(inner, exception.InnerException);
        Assert.False(exception.IsAuthFailure);
    }
}
