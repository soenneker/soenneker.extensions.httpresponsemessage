using System;
using System.Diagnostics.Contracts;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog;
using Soenneker.Extensions.String;
using Soenneker.Utils.Json;
using ILogger = Microsoft.Extensions.Logging.ILogger;

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
    public static async ValueTask EnsureSuccess(this System.Net.Http.HttpResponseMessage message, ILogger? logger = null)
    {
        if (message.IsSuccessStatusCode)
            return;

        string content = await message.Content.ReadAsStringAsync();

        if (logger != null)
            logger.LogInformation("{content}", content);
        else
            Log.Logger.Error(content);

        message.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Exception safe. Typically what we want if we're actually using this in code
    /// </summary>
    [Pure]
    public static async ValueTask<T?> To<T>(this System.Net.Http.HttpResponseMessage response)
    {
        string? content = null;

        Type serializationType = typeof(T);

        try
        {
            content = await response.Content.ReadAsStringAsync();

            object? result = JsonUtil.Deserialize(content, serializationType);

            if (result == null)
                throw new NullReferenceException("JSON deserialization was null");

            var castResult = (T)result;

            return castResult;
        }
        catch (Exception e)
        {
            Log.Error(e, "Could not read and parse content of type {type}, status code: {code}, with content: {content}", serializationType, (int)response.StatusCode, content);
        }

        return default;
    }

    /// <summary>
    /// Will throw an exception if it doesn't cast or deserialize properly (useful in tests) <para/>
    /// Reads content from message, and then deserializes
    /// </summary>
    /// <exception cref="Exception"></exception>
    public static async ValueTask<T> ToStrict<T>(this System.Net.Http.HttpResponseMessage response)
    {
        string? content = null;

        try
        {
            content = await response.Content.ReadAsStringAsync();

            if (content.IsNullOrEmpty())
                throw new Exception("Trying to deserialize empty string");

            object? result = JsonUtil.Deserialize(content, typeof(T));

            if (result == null)
                throw new NullReferenceException("JSON object after deserializing was null");

            var castResult = (T)result;

            return castResult;
        }
        catch (Exception e)
        {
            Log.Logger.Error(e, "Could not read and parse content of type {type}, with content: {content}", typeof(T), content);
            throw;
        }
    }

    /// <summary>
    /// Shorthand for response.Content.ReadAsStringAsync(). Exception safe.
    /// </summary>
    /// <returns>Null when an exception is thrown and we can't read the content as string.</returns>
    [Pure]
    public static async ValueTask<string?> ToStr(this System.Net.Http.HttpResponseMessage response)
    {
        try
        {
            string content = await response.Content.ReadAsStringAsync();

            return content;
        }
        catch (Exception e)
        {
            Log.Logger.Error(e, "Could not read content as string");
            return null;
        }
    }

    /// <summary>
    /// Shorthand for Log.Debug(response.Content.ReadAsStringAsync(). Not exception safe.
    /// </summary>
    public static async ValueTask LogResponse(this System.Net.Http.HttpResponseMessage response)
    {
        string content = await response.Content.ReadAsStringAsync();

        Log.Debug("{content}", content);
    }
}