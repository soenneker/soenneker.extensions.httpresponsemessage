using Microsoft.Extensions.Logging;
using Soenneker.Dtos.ProblemDetails;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Json;
using Soenneker.Utils.Xml;
using System;
using System.Diagnostics.Contracts;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
    public static async System.Threading.Tasks.ValueTask EnsureSuccess(this System.Net.Http.HttpResponseMessage message, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        if (message.IsSuccessStatusCode)
            return;

        string content = await message.ToStringStrict(cancellationToken: cancellationToken).NoSync();

        logger?.LogInformation("HTTP Content: {content}", content);

        message.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Exception-safe method for asynchronously deserializing an <see cref="HttpResponseMessage"/> 
    /// into an instance of type <typeparamref name="TResponse"/>.
    /// This is typically the desired behavior for production code.
    /// </summary>
    /// <typeparam name="TResponse">The type of the expected response.</typeparam>
    /// <param name="response">The HTTP response message to process.</param>
    /// <param name="logger">An optional logger for recording warnings and errors.</param>
    /// <param name="cancellationToken">A cancellation token to manage the operation's lifecycle.</param>
    /// <returns>
    /// The deserialized response of type <typeparamref name="TResponse"/>. 
    /// Returns <c>default</c> if deserialization fails or if the response content is null or empty.
    /// </returns>
    /// <remarks>
    /// This method logs a warning if the response content is null or empty, 
    /// and it logs an error if deserialization fails. 
    /// Ensure that the <paramref name="logger"/> is configured to capture these logs.
    /// </remarks>
    [Pure]
    public static async ValueTask<TResponse?> To<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        try
        {
            TResponse? result = await JsonUtil.Deserialize<TResponse>(response, logger, cancellationToken).NoSync();

            if (result is not null)
                return result;

            await LogWarning(logger, typeof(TResponse), response, cancellationToken).NoSync();
            return default;
        }
        catch (Exception e) // TODO: get more strict with exception
        {
            await LogError(logger, e, typeof(TResponse), response, cancellationToken).NoSync();
        }

        return default;
    }

    [Pure]
    public static async ValueTask<TResponse?> ToFromXml<TResponse>(System.Net.Http.HttpResponseMessage response, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        try
        {
            string content = await response.Content.ReadAsStringAsync(cancellationToken).NoSync();

            var result = XmlUtil.Deserialize<TResponse>(content);

            if (result == null)
                throw new NullReferenceException("XML deserialization was null");

            return result;
        }
        catch (Exception e)
        {
            await LogError(logger, e, typeof(TResponse), response, cancellationToken).NoSync();
        }

        return default;
    }

    /// <summary>
    /// Asynchronously processes an <see cref="HttpResponseMessage"/> to extract a response of type <typeparamref name="TResponse"/> 
    /// and optional <see cref="ProblemDetailsDto"/>. 
    /// </summary>
    /// <typeparam name="TResponse">The type of the expected response.</typeparam>
    /// <param name="response">The HTTP response message to process.</param>
    /// <param name="logger">An optional logger for logging warnings and errors.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A tuple containing the deserialized response of type <typeparamref name="TResponse"/> and optional <see cref="ProblemDetailsDto"/>.
    /// If deserialization fails or the content is null/empty, returns default values.</returns>
    /// <remarks>
    /// This method will log warnings if the content is null/empty, and it will log errors 
    /// if deserialization of <typeparamref name="TResponse"/> fails. If <typeparamref name="TResponse"/> 
    /// cannot be deserialized but <see cref="ProblemDetailsDto"/> can, it returns the problem details.
    /// </remarks>
    [Pure]
    public static async ValueTask<(TResponse?, ProblemDetailsDto?)> ToWithDetails<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (response.IsSuccessStatusCode)
            {
                TResponse? result = await JsonUtil.Deserialize<TResponse>(response, logger, cancellationToken).NoSync();

                if (result is not null)
                    return (result, null);
            }
            else
            {
                ProblemDetailsDto? problemDetails = await JsonUtil.Deserialize<ProblemDetailsDto>(response, logger, cancellationToken).NoSync();

                if (problemDetails is not null)
                    return (default, problemDetails);
            }

            await LogWarning(logger, typeof(TResponse), response, cancellationToken).NoSync();
            return (default, null);
        }
        catch (Exception e)
        {
            await LogError(logger, e, typeof(TResponse), response, cancellationToken).NoSync();
        }

        return (default, null);
    }

    /// <summary>
    /// Will throw an exception if it doesn't cast or deserialize properly. Useful in tests or retry logic. <para/>
    /// Reads content from message, and then deserializes
    /// </summary>
    /// <exception cref="Exception"></exception>
    public static async ValueTask<TResponse> ToStrict<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        try
        {
            TResponse? result = await JsonUtil.Deserialize<TResponse>(response, logger, cancellationToken).NoSync();

            if (result is not null)
                return result;
        }
        catch (Exception e)
        {
            await LogError(logger, e, typeof(TResponse), response, cancellationToken).NoSync();
            throw;
        }

        throw new JsonException($"Failed to deserialize ({typeof(TResponse).Name})");
    }

    /// <summary>
    /// Shorthand for response.Content.ReadAsStringAsync(). Exception safe.
    /// </summary>
    /// <returns>Null when an exception is thrown, and we can't read the content as string.</returns>
    [Pure]
    public static async ValueTask<string?> ToStringSafe(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await response.ToStringStrict(cancellationToken: cancellationToken).NoSync();
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
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    [Pure]
    public static Task<string> ToStringStrict(this System.Net.Http.HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        return response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Shorthand for Log.Debug(response.Content.ReadAsStringAsync(). Not exception safe.
    /// </summary>
    public static async System.Threading.Tasks.ValueTask LogResponse(this System.Net.Http.HttpResponseMessage response, ILogger logger, CancellationToken cancellationToken = default)
    {
        string content = await response.ToStringStrict(cancellationToken: cancellationToken).NoSync();

        logger.LogDebug("{content}", content);
    }

    private static async System.Threading.Tasks.ValueTask LogWarning(ILogger? logger, Type responseType, System.Net.Http.HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string responseContent = await response.Content.ReadAsStringAsync(cancellationToken).NoSync();

        logger?.LogWarning("Deserialization of type ({type}) was null, status code: {code}, content: {responseContent}",
            responseType.Name, (int) response.StatusCode, responseContent);
    }

    private static async System.Threading.Tasks.ValueTask LogError(ILogger? logger, Exception exception, Type responseType, System.Net.Http.HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string responseContent = await response.Content.ReadAsStringAsync(cancellationToken).NoSync();

        logger?.LogError(exception, "Deserialization of type {type} failed, status code: {code}, with content: {content}",
            responseType.Name, (int) response.StatusCode, responseContent);
    }
}