using System;
using System.Diagnostics.Contracts;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Extensions.String;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Json;

namespace Soenneker.Extensions.HttpResponseMessage;

/// <summary>
/// A collection of helpful HttpResponseMessage extension methods
/// </summary>
public static class HttpResponseMessageExtension
{
    /// <summary>
    /// Reads the content of the message, logs it, and then calls EnsureSuccessStatusCode <para/>
    /// Useful in tests
    /// TODO: More work here, we should be looking at ProblemDetails and such
    /// </summary>
    /// <exception cref="HttpRequestException"></exception>
    public static async System.Threading.Tasks.ValueTask EnsureSuccess(this System.Net.Http.HttpResponseMessage message, ILogger? logger = null)
    {
        if (message.IsSuccessStatusCode)
            return;

        string content = await message.ToStringStrict().NoSync();

        logger?.LogInformation("HTTP Content: {content}", content);

        message.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Exception safe. This is typically what we want for live code.
    /// </summary>
    [Pure]
    public static async ValueTask<TResponse?> To<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null)
    {
        string? content = null;

        try
        {
            content = await response.ToStringStrict().NoSync();

            var result = JsonUtil.Deserialize<TResponse>(content);

            if (result == null)
            {
                logger?.LogWarning("Deserialization of type ({type}) resulted in null, content: {responseContent}", typeof(TResponse).Name, content);
                return default;
            }
        }
        catch (Exception e) // TODO: get more strict with exception
        {
            logger?.LogError(e, "Could not read and parse content of type {type}, status code: {code}, with content: {content}", typeof(TResponse).Name, (int) response.StatusCode, content);
        }

        return default;
    }

    /// <summary>
    /// Will throw an exception if it doesn't cast or deserialize properly. Useful in tests or retry logic. <para/>
    /// Reads content from message, and then deserializes
    /// </summary>
    /// <exception cref="Exception"></exception>
    public static async ValueTask<TResponse> ToStrict<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null)
    {
        string? content = null;

        try
        {
            content = await response.ToStringStrict().NoSync();

            if (content.IsNullOrEmpty())
                throw new JsonException($"Trying to deserialize empty string for type ({typeof(TResponse).Name}), skipping");

            var result = JsonUtil.Deserialize<TResponse>(content);

            if (result != null)
                return result;
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Could not read and parse content of type {type}, with content: {content}", typeof(TResponse).Name, content);
            throw;
        }

        throw new JsonException($"Failed to deserialize ({typeof(TResponse).Name})");
    }

    /// <summary>
    /// Shorthand for response.Content.ReadAsStringAsync(). Exception safe.
    /// </summary>
    /// <returns>Null when an exception is thrown, and we can't read the content as string.</returns>
    [Pure]
    public static async ValueTask<string?> ToStringSafe(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null)
    {
        try
        {
            return await response.ToStringStrict().NoSync();
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Could not read content as string");
            return null;
        }
    }

    /// <summary>
    /// response.Content.ReadAsStringAsync()
    /// </summary>
    /// <param name="response"></param>
    /// <returns></returns>
    [Pure]
    public static async ValueTask<string> ToStringStrict(this System.Net.Http.HttpResponseMessage response)
    {
        return await response.Content.ReadAsStringAsync().NoSync();
    }

    /// <summary>
    /// Shorthand for Log.Debug(response.Content.ReadAsStringAsync(). Not exception safe.
    /// </summary>
    public static async System.Threading.Tasks.ValueTask LogResponse(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null)
    {
        string content = await response.ToStringStrict().NoSync();

        logger?.LogDebug("{content}", content);
    }
}