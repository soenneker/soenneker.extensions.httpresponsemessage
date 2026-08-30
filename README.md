[![](https://img.shields.io/nuget/v/Soenneker.Extensions.HttpResponseMessage.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Extensions.HttpResponseMessage/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.extensions.httpresponsemessage/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.extensions.httpresponsemessage/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/Soenneker.Extensions.HttpResponseMessage.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Extensions.HttpResponseMessage/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.extensions.httpresponsemessage/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.extensions.httpresponsemessage/actions/workflows/codeql.yml)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.Extensions.HttpResponseMessage
Response-body conversion, status-aware results, media-type inspection, and bounded diagnostic logging for `HttpResponseMessage`.

## Installation

```bash
dotnet add package Soenneker.Extensions.HttpResponseMessage
```

## Require a successful JSON response

```csharp
using Soenneker.Extensions.HttpResponseMessage;

using HttpResponseMessage response = await httpClient.GetAsync(uri, cancellationToken);

await response.EnsureSuccess(logger, cancellationToken);
OrderDto order = await response.ToStrict<OrderDto>(logger, cancellationToken);
```

`EnsureSuccess()` returns immediately for a successful status. For a failure it can log a non-binary, small body at `Information`, then throws through `EnsureSuccessStatusCode()`. `ToStrict<T>()` requires a nonempty JSON body and throws when reading or deserialization fails; it does not check the HTTP status itself.

Both methods consume the response body. Keep the response alive until conversion finishes and dispose it afterward.

## Convert without throwing on malformed content

```csharp
CustomerDto? customer = await response.To<CustomerDto>(logger, cancellationToken);
```

`To<T>()` returns `default` for no content, unreadable content, non-JSON content, and deserialization failures. It does not treat a non-success status as a conversion failure: if that response contains compatible JSON, it can still return `T`. Read and conversion failures can be logged; cancellation requested through the supplied token still propagates.

Use `ToWithString<T>()` when the raw response text is needed alongside the attempted conversion:

```csharp
(CustomerDto? customer, string? raw) =
    await response.ToWithString<CustomerDto>(logger, cancellationToken);
```

The tuple contains the raw body even when JSON conversion fails. A read failure produces `(default, null)`, while a known no-content response produces `(default, "")`.

For XML, `ToFromXml<T>()` follows the forgiving pattern and returns `default` on read or deserialization failure.

## Produce an operation result

```csharp
OperationResult<CustomerDto> result =
    await response.ToResult<CustomerDto>(logger, cancellationToken);
```

`ToResult<T>()` is status-aware:

- A successful response with valid JSON becomes a successful result containing `T`.
- A non-success response containing `ProblemDetailsDto` becomes a failed result with those details.
- Missing, malformed, or unexpected content becomes a failed result with the package's generic error message.
- Status `204`, status `205`, or a declared zero-length body becomes an empty result carrying the HTTP status.

Overloads accepting `JsonTypeInfo<T>` are available for source-generated JSON metadata on `To<T>()`, `ToStrict<T>()`, and `ToResult<T>()`.

## Read or log response text

```csharp
string? body = await response.ToStringSafe(logger, cancellationToken);
string bodyOrThrow = await response.ToStringStrict(cancellationToken);
await response.LogResponse(logger, cancellationToken);
```

`ToStringSafe()` returns `null` on read failure and an empty string for recognized binary media types. `ToStringStrict()` delegates to `ReadAsStringAsync()` and lets failures propagate. `LogResponse()` writes a debug preview only when debug logging is enabled, the media type is not recognized as binary, and the declared body size is small enough to buffer.

Conversion warnings and errors can include a preview of response content. Do not enable these logs for secrets, credentials, or personal data unless the destination and retention policy are appropriate.

## Inspect response metadata

- `IsNoContent()` recognizes status `204`, status `205`, and a declared `Content-Length` of zero.
- `IsJson()` recognizes `application/json` and media types ending in `+json`.
- `IsProblemJson()` recognizes `application/problem+json`.
- `IsXml()` recognizes `application/xml`, `text/xml`, and media types ending in `+xml`.
- `LooksBinary()` recognizes image, audio, video, and `application/octet-stream` media types.

These checks use response metadata; they do not inspect or validate the complete body.
