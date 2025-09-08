using Microsoft.Extensions.Logging;
using Soenneker.Dtos.ProblemDetails;
using Soenneker.Dtos.Results.Operation;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Xml;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Contracts;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.HttpContent;
using Soenneker.Extensions.Spans.Readonly;
using Soenneker.Extensions.Stream;
using Soenneker.Extensions.Task;
using Soenneker.Utils.Json;
using Soenneker.Extensions.String;

namespace Soenneker.Extensions.HttpResponseMessage;

/// <summary>A collection of helpful HttpResponseMessage extension methods with minimal allocations.</summary>
public static class HttpResponseMessageExtension
{
    private const int _logPreviewMaxChars = 4_096;

    // Thread-safe cache for charsets to Encoding to avoid repeated GetEncoding costs.
    private static readonly ConcurrentDictionary<string, Encoding> _encCache = new(StringComparer.OrdinalIgnoreCase);

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
            string preview = GetContentPreview(bytes, message.Content.Headers.ContentType?.CharSet);
            logger.LogInformation("HTTP Content: {content}", preview);
        }

        message.EnsureSuccessStatusCode();
    }

    /// <summary>Exception-safe JSON to T from the response body (returns default on failure).</summary>
    [Pure, MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static async ValueTask<TResponse?> To<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (IsNoContent(response))
            return default;

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

            ReadOnlySpan<byte> span = bytes.Span;
            bool looksJson = span.LooksLikeJson();

            if (!looksJson)
            {
                LogWarning(logger, typeof(TResponse), response, bytes);
                return default;
            }

            try
            {
                if (JsonUtil.TryDeserialize(span, out TResponse? result))
                    return result;

                LogWarning(logger, typeof(TResponse), response, bytes);
            }
            catch (Exception e)
            {
                LogError(logger, e, typeof(TResponse), response, bytes);
            }

            return default;
        }

        // Stream path: small peek to classify, then replay
        try
        {
            await using System.IO.Stream s = await response.Content.ReadAsStreamAsync(cancellationToken).NoSync();

            byte[] head = ArrayPool<byte>.Shared.Rent(1024);
            try
            {
                int read = await s.ReadAsync(head, 0, 1024, cancellationToken).NoSync();

                if (read == 0)
                {
                    LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                    return default;
                }

                ReadOnlySpan<byte> span = head.AsSpan(0, read);
                bool looksJson = span.LooksLikeJson();

                if (!looksJson)
                {
                    LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                    return default;
                }

                // Rebuild a stream with head + remainder
                var ms = new MemoryStream(read + 4096); // small headroom; CopyToAsync will grow if needed
                if (read > 0)
                    ms.Write(head, 0, read);

                await s.CopyToAsync(ms, cancellationToken).NoSync();
                ms.ToStart();

                TResponse? result = await JsonUtil.Deserialize<TResponse>(ms, logger, cancellationToken).NoSync();

                if (result is not null)
                    return result;

                LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                return default;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head);
            }
        }
        catch (Exception e)
        {
            LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return default;
        }
    }

    /// <summary>Deserialize to T and also return the raw string (if needed).</summary>
    [Pure, MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static async ValueTask<(TResponse? response, string? content)> ToWithString<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (IsNoContent(response))
            return (default, string.Empty);

        // We always return the string for this API => prefer bytes path (so we can decode once).
        ReadOnlyMemory<byte> bytes;

        try
        {
            bytes = await response.Content.GetSmallContentBytes(cancellationToken).NoSync();

            // If large/unknown length AND bytes are empty (because the helper didn't buffer), fall back to string once.
            if (bytes.Length == 0 && response.Content.ShouldUseStream())
            {
                string s = await response.Content.ReadAsStringAsync(cancellationToken).NoSync();

                if (TryDeserializeFromString<TResponse>(s, out TResponse? fromString))
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
            ReadOnlySpan<byte> span = bytes.Span;
            bool looksJson = span.LooksLikeJson();

            TResponse? result = looksJson && JsonUtil.TryDeserialize(span, out TResponse? r) ? r : default;

            string content = GetContentString(bytes, response.Content.Headers.ContentType?.CharSet);

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
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static async ValueTask<TResponse?> ToFromXml<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (IsNoContent(response))
            return default;

        // Prefer stream for large/unknown
        if (response.Content.ShouldUseStream())
        {
            try
            {
                await using System.IO.Stream s = await response.Content.ReadAsStreamAsync(cancellationToken).NoSync();

                var result = XmlUtil.Deserialize<TResponse>(s);

                return result ?? throw new NullReferenceException("XML deserialization returned null");
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

            string xml = GetContentString(bytes, response.Content.Headers.ContentType?.CharSet);

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
    [Pure, MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

                ProblemDetailsDto? pd = await JsonUtil.Deserialize<ProblemDetailsDto>(s, logger, cancellationToken).NoSync();

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

        // General case: success -> TResponse ; failure -> ProblemDetailsDto if JSON (content-based)
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
                ReadOnlySpan<byte> span = bytes.Span;
                bool looksJson = span.LooksLikeJson();

                if (response.IsSuccessStatusCode && looksJson)
                {
                    if (JsonUtil.TryDeserialize(span, out TResponse? ok))
                        return (ok, null);
                }
                else if (looksJson)
                {
                    if (JsonUtil.TryDeserialize(span, out ProblemDetailsDto? problem) && problem is not null)
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

            byte[] head = ArrayPool<byte>.Shared.Rent(1024);

            try
            {
                int read = await s.ReadAsync(head, 0, 1024, cancellationToken).NoSync();

                if (read == 0)
                {
                    LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                    return (default, null);
                }

                ReadOnlySpan<byte> span = head.AsSpan(0, read);
                bool looksJson = span.LooksLikeJson();

                var ms = new MemoryStream(read + 4096);
                if (read > 0)
                    ms.Write(head, 0, read);

                await s.CopyToAsync(ms, cancellationToken).NoSync();
                ms.ToStart();

                if (response.IsSuccessStatusCode && looksJson)
                {
                    TResponse? ok = await JsonUtil.Deserialize<TResponse>(ms, logger, cancellationToken).NoSync();

                    if (ok is not null)
                        return (ok, null);
                }
                else if (looksJson)
                {
                    ProblemDetailsDto? problem = await JsonUtil.Deserialize<ProblemDetailsDto>(ms, logger, cancellationToken).NoSync();
                    if (problem is not null) 
                        return (default, problem);
                }

                LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                return (default, null);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head);
            }
        }
        catch (Exception e)
        {
            LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return (default, null);
        }
    }

    /// <summary>OperationResult wrapper using single buffered read.</summary>
    [Pure, MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
                ReadOnlySpan<byte> span = bytes.Span;
                bool looksJson = span.LooksLikeJson();

                if (response.IsSuccessStatusCode && looksJson)
                {
                    if (JsonUtil.TryDeserialize(span, out TResponse? ok) && ok is not null)
                        return OperationResult.Success(ok, response.StatusCode);
                }
                else if (looksJson)
                {
                    if (JsonUtil.TryDeserialize(span, out ProblemDetailsDto? problem) && problem is not null)
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

            byte[] head = ArrayPool<byte>.Shared.Rent(1024);
            try
            {
                int read = await s.ReadAsync(head, 0, 1024, cancellationToken).NoSync();

                if (read == 0)
                {
                    LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                    return null;
                }

                ReadOnlySpan<byte> span = head.AsSpan(0, read);
                bool looksJson = span.LooksLikeJson();

                var ms = new MemoryStream(read + 4096);
                if (read > 0)
                    ms.Write(head, 0, read);

                await s.CopyToAsync(ms, cancellationToken).NoSync();
                ms.ToStart();

                if (response.IsSuccessStatusCode && looksJson)
                {
                    TResponse? ok = await JsonUtil.Deserialize<TResponse>(ms, logger, cancellationToken).NoSync();
                    if (ok is not null) return OperationResult.Success(ok, response.StatusCode);
                }
                else if (looksJson)
                {
                    ProblemDetailsDto? problem = await JsonUtil.Deserialize<ProblemDetailsDto>(ms, logger, cancellationToken).NoSync();
                    if (problem is not null) return new OperationResult<TResponse> { Problem = problem, StatusCode = (int)response.StatusCode };
                }

                LogWarning(logger, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(head);
            }
        }
        catch (Exception e)
        {
            LogError(logger, e, typeof(TResponse), response, ReadOnlyMemory<byte>.Empty);
            return null;
        }
    }

    /// <summary>Strict JSON to T (throws on failure).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static async ValueTask<TResponse> ToStrict<TResponse>(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (IsNoContent(response))
            throw new JsonException($"Failed to deserialize ({typeof(TResponse).Name}) - no content");

        if (response.Content is null)
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
    [Pure, MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static async ValueTask<string?> ToStringSafe(this System.Net.Http.HttpResponseMessage response, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (LooksBinary(response))
                return string.Empty;

            ReadOnlyMemory<byte> bytes = await response.Content.GetSmallContentBytes(cancellationToken).NoSync();

            if (bytes.Length == 0 && response.Content.ShouldUseStream())
                return await response.Content.ReadAsStringAsync(cancellationToken).NoSync();

            return GetContentString(bytes, response.Content.Headers.ContentType?.CharSet);
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
        string preview = GetContentPreview(bytes, response.Content.Headers.ContentType?.CharSet);
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static string GetContentString(ReadOnlyMemory<byte> bytes, string? headerCharset)
    {
        if (bytes.Length == 0)
            return string.Empty;

        return ResolveEncoding(headerCharset).GetString(bytes.Span);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static string GetContentPreview(ReadOnlyMemory<byte> bytes, string? headerCharset)
    {
        if (bytes.Length == 0)
            return string.Empty;

        // Decode no more than needed for preview. Use UTF-8 max bound for speed; mixed charsets still OK for preview.
        int maxBytes = Math.Min(bytes.Length, Encoding.UTF8.GetMaxByteCount(_logPreviewMaxChars));
        ReadOnlyMemory<byte> slice = bytes[..maxBytes];

        string s = ResolveEncoding(headerCharset).GetString(slice.Span);

        if (s.Length > _logPreviewMaxChars)
            return s[.._logPreviewMaxChars] + "…";

        return s;
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (charset.IsNullOrWhiteSpace())
            return Encoding.UTF8;

        // cheap normalization
        if (charset.Equals("utf8", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8;

        return _encCache.GetOrAdd(charset, key =>
        {
            try
            {
                return Encoding.GetEncoding(key);
            }
            catch
            {
                return Encoding.UTF8;
            }
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryDeserializeFromString<T>(string json, out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json);
            return value is not null;
        }
        catch
        {
            value = default;
            return false;
        }
    }

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