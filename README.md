[![](https://img.shields.io/nuget/v/Soenneker.Extensions.HttpResponseMessage.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Extensions.HttpResponseMessage/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.extensions.httpresponsemessage/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.extensions.httpresponsemessage/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/Soenneker.Extensions.HttpResponseMessage.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Extensions.HttpResponseMessage/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.extensions.httpresponsemessage/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.extensions.httpresponsemessage/actions/workflows/codeql.yml)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.Extensions.HttpResponseMessage
A collection of helpful HttpResponseMessage extension methods.

## Installation

```bash
dotnet add package Soenneker.Extensions.HttpResponseMessage
```

## Quick start

```csharp
using Soenneker.Extensions.HttpResponseMessage;

// Given an existing System.Net.Http.HttpResponseMessage named message:
var result = message.EnsureSuccess();
```

## Common operations

- `EnsureSuccess()` - Reads the content (only if needed for logging), logs it, then calls EnsureSuccessStatusCode. Useful in tests.
- `To()` - Exception-safe JSON to T from the response body (returns default on failure).
- `ToFromXml()` - XML deserialize to T (exception-safe, default on failure).
- `ToResult()` - OperationResult wrapper using single buffered read.
- `ToStrict()` - Strict JSON to T (throws on failure).
- `ToStringSafe()` - Exception-safe content->string (returns null on failure).
- `ToStringStrict()` - Raw content as string (throws on failure).
- `LogResponse()` - Log response body at Debug (single read & capped).
- `IsNoContent()` - Returns `true` when the HTTP status code is `204 No Content`.
- `IsJson()` - Returns `true` when the response content type is JSON, including structured `+json` media types.
- `IsProblemJson()` - Returns `true` when the response uses the `application/problem+json` problem-details media type.
- `IsXml()` - Returns `true` when the response content type is XML, including structured `+xml` media types.

The package also includes one additional operation for more specialized cases.
