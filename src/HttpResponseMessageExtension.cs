using Microsoft.Extensions.Logging;
using Soenneker.Dtos.ProblemDetails;
using Soenneker.Dtos.Results.Operation;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Xml;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.HttpContent;
using Soenneker.Extensions.Task;
using Soenneker.Utils.Json;
using Soenneker.Extensions.String;

namespace Soenneker.Extensions.HttpResponseMessage;

/// <summary>A collection of helpful HttpResponseMessage extension methods with minimal allocations.</summary>
public static class HttpResponseMessageExtension
{
    private const int _logPreviewMaxChars = 4_096;

    // Common charset cache to avoid repeated Encoding.GetEncoding costs.
    private static readonly Dictionary<string, Encoding> _encCache = new(StringComparer.OrdinalIgnoreCase)
    {
        ["utf-8"] = Encoding.UTF8,
        ["utf8"] = Encoding.UTF8,
        ["us-ascii"] = Encoding.ASCII,
        ["ascii"] = Encoding.ASCII,
        ["utf-16"] = Encoding.Unicode,
        ["utf-16le"] = Encoding.Unicode,
        ["utf-16be"] = Encoding.BigEndianUnicode
    };

    /// <summary>
    /// Reads the content (only if needed for logging), logs it, then calls EnsureSuccessStatusCode.
    /// Useful in tests.
    /// </summary>
    public static async System.Threading.Tasks.ValueTask EnsureSuccess(this System.Net.Http.HttpResponseMessage message, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (message.IsSuccessStatusCode)
            return;

        if (logger is not null && logger.IsEnabled(LogLevel.Information) && !LooksBinary(message))
        {
            ReadOnlyMemory<byte> bytes = await message.Content.GetSmallContentBytes(cancellationToken).NoSync();
            string preview = GetContentPreview(bytes, message.Content?.Headers?.ContentType?.CharSet);
            logger.LogInformation("HTTP Content: {content}", preview);
        }

        message.EnsureSuccessStatusCode();
    }

    /// <summary>Exception-safe JSON to T from the response body (returns default on failure).</summary>
    [Pure]
    public static async ValueTask<TResponse?> To<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        // 204/205 and empty
        if (IsNoContent(response))
            return default;

        // If it's not JSON-ish, don't waste cycles trying.
        if (!IsJson(response))
        {
            LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return default;
        }

        // Small known bodies -> bytes; large/unknown -> stream
        if (!response.Content.ShouldUseStream())
        {
            ReadOnlyMemory<byte> bytes;

            try
            {
                bytes = await response.Content.GetSmallContentBytes(cancellationToken).NoSync();
            }
            catch (Exception e)
            {
                LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                return default;
            }

            if (bytes.Length == 0)
            {
                LogWarning(logger, typeof(TResponse), response, bytes);
                return default;
            }

            try
            {
                if (JsonUtil.TryDeserialize(bytes.Span, out TResponse? result))
                    return result;

                LogWarning(logger, typeof(TResponse), response, bytes);
            }
            catch (Exception e)
            {
                LogError(logger, e, typeof(TResponse), response, bytes);
            }

            return default;
        }

        try
        {
            await using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).NoSync();
            TResponse? result = await JsonUtil.Deserialize<TResponse>(stream, logger, cancellationToken).NoSync();

            if (result is not null)
                return result;

            LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return default;
        }
        catch (Exception e)
        {
            LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return default;
        }
    }

    /// <summary>Deserialize to T and also return the raw string (if needed).</summary>
    [Pure]
    public static async ValueTask<(TResponse? response, string? content)> ToWithString<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (IsNoContent(response))
            return (default, string.Empty);

        // We always return the string for this API => use bytes path (so we can decode once).
        ReadOnlyMemory<byte> bytes;

        try
        {
            bytes = await response.Content.GetSmallContentBytes(cancellationToken).NoSync();
            // If large/unknown length, fallback to full read (rare caller; OK to allocate)
            if (bytes.Length == 0 && response.Content.ShouldUseStream())
            {
                // Materialize string from stream directly (one allocation path)
                string s = await response.Content.ReadAsStringAsync(cancellationToken).NoSync();

                // Try JSON only if content-type suggests JSON
                if (IsJson(response) && JsonUtil.TryDeserialize(MemoryMarshalAsUtf8(s), out TResponse? fromString))
                    return (fromString, s);

                LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                return (default, s);
            }
        }
        catch (Exception e)
        {
            LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return (default, null);
        }

        try
        {
            TResponse? result = IsJson(response) && JsonUtil.TryDeserialize(bytes.Span, out TResponse? r) ? r : default;
            string content = GetContentString(bytes, response.Content?.Headers?.ContentType?.CharSet);

            if (result is not null)
                return (result, content);

            LogWarning(logger, typeof(TResponse), response, bytes);
            return (default, content);
        }
        catch (Exception e)
        {
            LogError(logger, e, typeof(TResponse), response, bytes);
            return (default, null);
        }
    }

    /// <summary>XML deserialize to T (exception-safe, default on failure).</summary>
    public static async ValueTask<TResponse?> ToFromXml<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (IsNoContent(response))
            return default;

        // If it’s clearly not XML, avoid the work (optional, but cheap)
        if (!IsXml(response))
        {
            LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return default;
        }

        // Prefer stream for large/unknown
        if (response.Content.ShouldUseStream())
        {
            try
            {
                await using System.IO.Stream s = await response.Content.ReadAsStreamAsync(cancellationToken).NoSync();
                // If XmlUtil has stream overload, use it. Otherwise, read string once (still streaming, but will allocate)
                // Example with string fallback:
                using var reader = new StreamReader(s, ResolveEncoding(response.Content.Headers.ContentType?.CharSet), detectEncodingFromByteOrderMarks: true);
                string xml = await reader.ReadToEndAsync(cancellationToken).NoSync();

                var result = XmlUtil.Deserialize<TResponse>(xml);

                if (result is null)
                    throw new NullReferenceException("XML deserialization returned null");

                return result;
            }
            catch (Exception e)
            {
                LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                return default;
            }
        }

        var bytes = ReadOnlyMemory<byte>.Empty;

        try
        {
            bytes = await response.Content.GetSmallContentBytes(cancellationToken).NoSync();

            if (bytes.Length == 0)
                throw new NullReferenceException("XML content empty");

            string xml = GetContentString(bytes, response.Content?.Headers?.ContentType?.CharSet);

            var result = XmlUtil.Deserialize<TResponse>(xml);
            if (result is null)
                throw new NullReferenceException("XML deserialization returned null");

            return result;
        }
        catch (Exception e)
        {
            LogError(logger, e, typeof(TResponse), response, bytes);
            return default;
        }
    }

    /// <summary>Deserialize to TResponse or ProblemDetailsDto (single read; no duplicate allocations).</summary>
    [Obsolete("ToResult should be used; this will be removed soon")]
    [Pure]
    public static async ValueTask<(TResponse? response, ProblemDetailsDto? details)> ToWithDetails<TResponse>(this System.Net.Http.HttpResponseMessage response,
        ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        if (IsNoContent(response))
            return (default, null);

        // Shortcut: if server says problem+json, decode that directly
        if (IsProblemJson(response))
        {
            if (!response.Content.ShouldUseStream())
            {
                ReadOnlyMemory<byte> b1;
                try
                {
                    b1 = await response.Content.GetSmallContentBytes(cancellationToken).NoSync();
                }
                catch (Exception e)
                {
                    LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                    return (default, null);
                }

                if (JsonUtil.TryDeserialize(b1.Span, out ProblemDetailsDto? pd) && pd is not null)
                    return (default, pd);

                LogWarning(logger, typeof(TResponse), response, b1);
                return (default, null);
            }

            try
            {
                await using System.IO.Stream s = await response.Content.ReadAsStreamAsync(cancellationToken).NoSync();

                ProblemDetailsDto? pd = await JsonUtil.Deserialize<ProblemDetailsDto>(s, null, cancellationToken).NoSync();

                if (pd is not null)
                    return (default, pd);

                LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                return (default, null);
            }
            catch (Exception e)
            {
                LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                return (default, null);
            }
        }

        // General case: success -> TResponse ; failure -> ProblemDetailsDto if JSON
        if (!response.Content.ShouldUseStream())
        {
            ReadOnlyMemory<byte> bytes;

            try
            {
                bytes = await response.Content.GetSmallContentBytes(cancellationToken).NoSync();
            }
            catch (Exception e)
            {
                LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                return (default, null);
            }

            try
            {
                if (response.IsSuccessStatusCode && IsJson(response))
                {
                    if (JsonUtil.TryDeserialize(bytes.Span, out TResponse? ok))
                        return (ok, null);
                }
                else if (IsJson(response))
                {
                    if (JsonUtil.TryDeserialize(bytes.Span, out ProblemDetailsDto? problem) && problem is not null)
                        return (default, problem);
                }

                LogWarning(logger, typeof(TResponse), response, bytes);
                return (default, null);
            }
            catch (Exception e)
            {
                LogError(logger, e, typeof(TResponse), response, bytes);
                return (default, null);
            }
        }

        try
        {
            await using System.IO.Stream s = await response.Content.ReadAsStreamAsync(cancellationToken).NoSync();

            if (response.IsSuccessStatusCode && IsJson(response))
            {
                TResponse? ok = await JsonUtil.Deserialize<TResponse>(s, null, cancellationToken).NoSync();
                if (ok is not null) return (ok, null);
            }
            else if (IsJson(response))
            {
                ProblemDetailsDto? problem = await JsonUtil.Deserialize<ProblemDetailsDto>(s, logger, cancellationToken).NoSync();
                if (problem is not null) return (default, problem);
            }

            LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return (default, null);
        }
        catch (Exception e)
        {
            LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return (default, null);
        }
    }

    /// <summary>OperationResult wrapper using single buffered read.</summary>
    [Pure]
    public static async ValueTask<OperationResult<TResponse>?> ToResult<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (IsNoContent(response))
            return null;

        if (!response.Content.ShouldUseStream())
        {
            ReadOnlyMemory<byte> bytes;

            try
            {
                bytes = await response.Content.GetSmallContentBytes(cancellationToken).NoSync();
            }
            catch (Exception e)
            {
                LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                return null;
            }

            try
            {
                if (response.IsSuccessStatusCode && IsJson(response))
                {
                    if (JsonUtil.TryDeserialize(bytes.Span, out TResponse? ok) && ok is not null)
                        return OperationResult.Success(ok, response.StatusCode);
                }
                else if (IsJson(response))
                {
                    if (JsonUtil.TryDeserialize(bytes.Span, out ProblemDetailsDto? problem) && problem is not null)
                        return new OperationResult<TResponse> { Problem = problem, StatusCode = (int)response.StatusCode };
                }

                LogWarning(logger, typeof(TResponse), response, bytes);
                return null;
            }
            catch (Exception e)
            {
                LogError(logger, e, typeof(TResponse), response, bytes);
                return null;
            }
        }

        try
        {
            await using System.IO.Stream s = await response.Content.ReadAsStreamAsync(cancellationToken).NoSync();

            if (response.IsSuccessStatusCode && IsJson(response))
            {
                TResponse? ok = await JsonUtil.Deserialize<TResponse>(s, null, cancellationToken).NoSync();
                if (ok is not null) return OperationResult.Success(ok, response.StatusCode);
            }
            else if (IsJson(response))
            {
                ProblemDetailsDto? problem = await JsonUtil.Deserialize<ProblemDetailsDto>(s, null, cancellationToken).NoSync();
                if (problem is not null) return new OperationResult<TResponse> { Problem = problem, StatusCode = (int)response.StatusCode };
            }

            LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return null;
        }
        catch (Exception e)
        {
            LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return null;
        }
    }

    /// <summary>Strict JSON to T (throws on failure).</summary>
    public static async ValueTask<TResponse> ToStrict<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (IsNoContent(response))
            throw new JsonException($"Failed to deserialize ({typeof(TResponse).Name}) - no content");

        if (!response.Content.ShouldUseStream())
        {
            ReadOnlyMemory<byte> bytes = await response.Content.GetSmallContentBytes(cancellationToken).NoSync();

            try
            {
                if (JsonUtil.TryDeserialize(bytes.Span, out TResponse? ok) && ok is not null)
                    return ok;
            }
            catch (Exception e)
            {
                LogError(logger, e, typeof(TResponse), response, bytes);
                throw;
            }
        }
        else
        {
            try
            {
                await using System.IO.Stream s = await response.Content.ReadAsStreamAsync(cancellationToken).NoSync();
                TResponse? ok = await JsonUtil.Deserialize<TResponse>(s, logger, cancellationToken).NoSync();
                if (ok is not null) return ok;
            }
            catch (Exception e)
            {
                LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                throw;
            }
        }

        throw new JsonException($"Failed to deserialize ({typeof(TResponse).Name})");
    }

    /// <summary>Exception-safe content->string (returns null on failure).</summary>
    [Pure]
    public static async ValueTask<string?> ToStringSafe(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (LooksBinary(response))
                return string.Empty;

            ReadOnlyMemory<byte> bytes = await response.Content.GetSmallContentBytes(cancellationToken).NoSync();

            if (bytes.Length == 0 && response.Content.ShouldUseStream())
                return await response.Content.ReadAsStringAsync(cancellationToken).NoSync();

            return GetContentString(bytes, response.Content?.Headers?.ContentType?.CharSet);
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Could not read content as string");
            return null;
        }
    }

    /// <summary>Raw content as string (throws on failure).</summary>
    [Pure]
    public static Task<string> ToStringStrict(this System.Net.Http.HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        return response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Log response body at Debug (single read & capped).</summary>
    public static async System.Threading.Tasks.ValueTask LogResponse(this System.Net.Http.HttpResponseMessage response, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!logger.IsEnabled(LogLevel.Debug) || LooksBinary(response))
            return;

        ReadOnlyMemory<byte> bytes = await response.Content.GetSmallContentBytes(cancellationToken).NoSync();
        string preview = GetContentPreview(bytes, response.Content?.Headers?.ContentType?.CharSet);
        logger.LogDebug("{content}", preview);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNoContent(this System.Net.Http.HttpResponseMessage r)
    {
        var code = (int)r.StatusCode;

        if (code is 204 or 205)
            return true;

        long? len = r.Content.Headers.ContentLength;
        return len is 0;
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsJson(this System.Net.Http.HttpResponseMessage r)
    {
        string? ct = r.Content.Headers.ContentType?.MediaType;

        return ct != null && (ct.Equals("application/json", StringComparison.OrdinalIgnoreCase) || ct.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
                              ct.Equals("application/problem+json", StringComparison.OrdinalIgnoreCase));
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsProblemJson(this System.Net.Http.HttpResponseMessage r)
    {
        string? ct = r.Content.Headers.ContentType?.MediaType;
        return ct != null && ct.Equals("application/problem+json", StringComparison.OrdinalIgnoreCase);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsXml(this System.Net.Http.HttpResponseMessage r)
    {
        string? ct = r.Content.Headers.ContentType?.MediaType;

        if (ct is null)
            return false;

        return ct.Equals("application/xml", StringComparison.OrdinalIgnoreCase) || ct.Equals("text/xml", StringComparison.OrdinalIgnoreCase) ||
               ct.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool LooksBinary(this System.Net.Http.HttpResponseMessage r)
    {
        string? ct = r.Content.Headers.ContentType?.MediaType;

        if (ct is null)
            return false;

        return ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
               ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || ct.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetContentString(ReadOnlyMemory<byte> bytes, string? headerCharset)
    {
        if (bytes.Length == 0)
            return string.Empty;

        return ResolveEncoding(headerCharset).GetString(bytes.Span);
    }

    private static string GetContentPreview(ReadOnlyMemory<byte> bytes, string? headerCharset)
    {
        if (bytes.Length == 0)
            return string.Empty;

        // Heuristic: avoid decoding massive payloads for preview
        ReadOnlyMemory<byte> slice = bytes.Length > _logPreviewMaxChars * 4 ? bytes[..(_logPreviewMaxChars * 4)] : bytes;

        string s = GetContentString(slice, headerCharset);

        if (s.Length > _logPreviewMaxChars)
            return s[.._logPreviewMaxChars] + "…";

        return s;
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (charset.IsNullOrWhiteSpace())
            return Encoding.UTF8;

        if (_encCache.TryGetValue(charset, out Encoding? enc))
            return enc;

        try
        {
            enc = Encoding.GetEncoding(charset);
        }
        catch
        {
            enc = Encoding.UTF8;
        }

        // benign race is fine; or guard with lock if desired
        _encCache[charset] = enc;
        return enc;
    }

    // Handy helper to treat a string as UTF-8 bytes without allocating a second string (still allocates byte[] if used)
    private static ReadOnlySpan<byte> MemoryMarshalAsUtf8(string s) => Encoding.UTF8.GetBytes(s);

    private static void LogWarning(ILogger? logger, Type responseType, System.Net.Http.HttpResponseMessage response, ReadOnlyMemory<byte> bytes)
    {
        if (logger is null || !logger.IsEnabled(LogLevel.Warning) || LooksBinary(response))
            return;

        string preview = GetContentPreview(bytes, response.Content?.Headers?.ContentType?.CharSet);
        logger.LogWarning("Deserialization of type ({type}) was null, status code: {code}, content: {responseContent}", responseType.Name, (int)response.StatusCode, preview);
    }

    private static void LogError(ILogger? logger, Exception exception, Type responseType, System.Net.Http.HttpResponseMessage response, ReadOnlyMemory<byte> bytes)
    {
        if (logger is null || !logger.IsEnabled(LogLevel.Error) || LooksBinary(response))
            return;

        string preview = GetContentPreview(bytes, response.Content?.Headers?.ContentType?.CharSet);
        logger.LogError(exception, "Deserialization of type {type} failed, status code: {code}, with content: {content}", responseType.Name, (int)response.StatusCode, preview);
    }
}