// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Generators.StaticRouteHandlerModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.Http.Generators.Tests;

public abstract class RequestDelegateGeneratorTests : RequestDelegateGeneratorTestBase
{
    [Theory]
    [InlineData(@"app.MapGet(""/hello"", () => ""Hello world!"");", "MapGet", "Hello world!")]
    [InlineData(@"app.MapPost(""/hello"", () => ""Hello world!"");", "MapPost", "Hello world!")]
    [InlineData(@"app.MapDelete(""/hello"", () => ""Hello world!"");", "MapDelete", "Hello world!")]
    [InlineData(@"app.MapPut(""/hello"", () => ""Hello world!"");", "MapPut", "Hello world!")]
    [InlineData(@"app.MapGet(pattern: ""/hello"", handler: () => ""Hello world!"");", "MapGet", "Hello world!")]
    [InlineData(@"app.MapPost(handler: () => ""Hello world!"", pattern: ""/hello"");", "MapPost", "Hello world!")]
    [InlineData(@"app.MapDelete(pattern: ""/hello"", handler: () => ""Hello world!"");", "MapDelete", "Hello world!")]
    [InlineData(@"app.MapPut(handler: () => ""Hello world!"", pattern: ""/hello"");", "MapPut", "Hello world!")]
    public async Task MapAction_NoParam_StringReturn(string source, string httpMethod, string expectedBody)
    {
        var (result, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(result, (endpointModel) =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
            Assert.Equal(httpMethod, endpointModel.HttpMethod);
        });

        var httpContext = CreateHttpContext();
        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody);
    }

    public static object[][] MapAction_ExplicitQueryParam_StringReturn_Data
    {
        get
        {
            var expectedBody = "TestQueryValue";
            var fromQueryRequiredSource = """app.MapGet("/", ([FromQuery] string queryValue) => queryValue);""";
            var fromQueryWithNameRequiredSource = """app.MapGet("/", ([FromQuery(Name = "queryValue")] string parameterName) => parameterName);""";
            var fromQueryWithNullNameRequiredSource = """app.MapGet("/", ([FromQuery(Name = null)] string queryValue) => queryValue);""";
            var fromQueryNullableSource = """app.MapGet("/", ([FromQuery] string? queryValue) => queryValue ?? string.Empty);""";
            var fromQueryDefaultValueSource = """
#nullable disable
string getQueryWithDefault([FromQuery] string queryValue = null) => queryValue ?? string.Empty;
app.MapGet("/", getQueryWithDefault);
#nullable restore
""";

            return new[]
            {
                new object[] { fromQueryRequiredSource, expectedBody, 200, expectedBody },
                new object[] { fromQueryRequiredSource, null, 400, string.Empty },
                new object[] { fromQueryWithNameRequiredSource, expectedBody, 200, expectedBody },
                new object[] { fromQueryWithNameRequiredSource, null, 400, string.Empty },
                new object[] { fromQueryWithNullNameRequiredSource, expectedBody, 200, expectedBody },
                new object[] { fromQueryWithNullNameRequiredSource, null, 400, string.Empty },
                new object[] { fromQueryNullableSource, expectedBody, 200, expectedBody },
                new object[] { fromQueryNullableSource, null, 200, string.Empty },
                new object[] { fromQueryDefaultValueSource, expectedBody, 200, expectedBody },
                new object[] { fromQueryDefaultValueSource, null, 200, string.Empty },
            };
        }
    }

    [Theory]
    [MemberData(nameof(MapAction_ExplicitQueryParam_StringReturn_Data))]
    public async Task MapAction_ExplicitQueryParam_StringReturn(string source, string queryValue, int expectedStatusCode, string expectedBody)
    {
        var (results, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(results, (endpointModel) =>
        {
            Assert.Equal("/", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
            var p = Assert.Single(endpointModel.Parameters);
            Assert.Equal(EndpointParameterSource.Query, p.Source);
            Assert.Equal("queryValue", p.Name);
        });

        var httpContext = CreateHttpContext();
        if (queryValue is not null)
        {
            httpContext.Request.QueryString = new QueryString($"?queryValue={queryValue}");
        }

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody, expectedStatusCode);
    }

    [Fact]
    public async Task MapAction_SingleTimeOnlyParam_StringReturn()
    {
        var (results, compilation) = await RunGeneratorAsync("""
app.MapGet("/hello", ([FromQuery]TimeOnly p) => p.ToString("o"));
""");
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(results, endpointModel =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
            var p = Assert.Single(endpointModel.Parameters);
            Assert.Equal(EndpointParameterSource.Query, p.Source);
            Assert.Equal("p", p.Name);
        });

        var httpContext = CreateHttpContext();
        httpContext.Request.QueryString = new QueryString("?p=13:30");

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, "13:30:00.0000000");
        await VerifyAgainstBaselineUsingFile(compilation);
    }

    [Theory]
    [InlineData("DateOnly", "2023-02-20")]
    [InlineData("DateTime", "2023-02-20")]
    [InlineData("DateTimeOffset", "2023-02-20")]
    public async Task MapAction_SingleDateLikeParam_StringReturn(string parameterType, string result)
    {
        var (results, compilation) = await RunGeneratorAsync($$"""
app.MapGet("/hello", ([FromQuery]{{parameterType}} p) => p.ToString("yyyy-MM-dd"));
""");
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(results, endpointModel =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
            var p = Assert.Single(endpointModel.Parameters);
            Assert.Equal(EndpointParameterSource.Query, p.Source);
            Assert.Equal("p", p.Name);
        });

        var httpContext = CreateHttpContext();
        httpContext.Request.QueryString = new QueryString($"?p={result}");

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, result);
    }

    public static object[][] TryParsableParameters
    {
        get
        {
            var now = DateTime.Now;

            return new[]
            {
                    // string is not technically "TryParsable", but it's the special case.
                    new object[] { "string", "plain string", "plain string" },
                    new object[] { "int", "-42", -42 },
                    new object[] { "uint", "42", 42U },
                    new object[] { "bool", "true", true },
                    new object[] { "short", "-42", (short)-42 },
                    new object[] { "ushort", "42", (ushort)42 },
                    new object[] { "long", "-42", -42L },
                    new object[] { "ulong", "42", 42UL },
                    new object[] { "IntPtr", "-42", new IntPtr(-42) },
                    new object[] { "char", "A", 'A' },
                    new object[] { "double", "0.5", 0.5 },
                    new object[] { "float", "0.5", 0.5f },
                    new object[] { "Half", "0.5", (Half)0.5f },
                    new object[] { "decimal", "0.5", 0.5m },
                    new object[] { "Uri", "https://example.org", new Uri("https://example.org") },
                    new object[] { "DateTime", now.ToString("o"), now.ToUniversalTime() },
                    new object[] { "DateTimeOffset", "1970-01-01T00:00:00.0000000+00:00", DateTimeOffset.UnixEpoch },
                    new object[] { "TimeSpan", "00:00:42", TimeSpan.FromSeconds(42) },
                    new object[] { "Guid", "00000000-0000-0000-0000-000000000000", Guid.Empty },
                    new object[] { "Version", "6.0.0.42", new Version("6.0.0.42") },
                    new object[] { "BigInteger", "-42", new BigInteger(-42) },
                    new object[] { "IPAddress", "127.0.0.1", IPAddress.Loopback },
                    new object[] { "IPEndPoint", "127.0.0.1:80", new IPEndPoint(IPAddress.Loopback, 80) },
                    new object[] { "AddressFamily", "Unix", AddressFamily.Unix },
                    new object[] { "ILOpCode", "Nop", ILOpCode.Nop },
                    new object[] { "AssemblyFlags", "PublicKey,Retargetable", AssemblyFlags.PublicKey | AssemblyFlags.Retargetable },
                    new object[] { "int?", "42", 42 },
                    new object[] { "int?", null, null },
                };
        }
    }

    [Theory]
    [MemberData(nameof(TryParsableParameters))]
    public async Task MapAction_TryParsableImplicitRouteParameters(string typeName, string routeValue, object expectedParameterValue)
    {
        var (results, compilation) = await RunGeneratorAsync($$"""
app.MapGet("/{routeValue}", (HttpContext context, {{typeName}} routeValue) =>
{
    context.Items["tryParsable"] = routeValue;
});
""");
        var endpoint = GetEndpointFromCompilation(compilation);
        var httpContext = CreateHttpContext();
        httpContext.Request.RouteValues["routeValue"] = routeValue;

        await endpoint.RequestDelegate(httpContext);
        Assert.Equal(200, httpContext.Response.StatusCode);
        Assert.Equal(expectedParameterValue, httpContext.Items["tryParsable"]);
    }

    [Theory]
    [MemberData(nameof(TryParsableParameters))]
    public async Task MapAction_TryParsableImplicitQueryParameters(string typeName, string queryStringInput, object expectedParameterValue)
    {
        var (results, compilation) = await RunGeneratorAsync($$"""
app.MapGet("/", (HttpContext context, {{typeName}} p) =>
{
    context.Items["tryParsable"] = p;
});
""");
        var endpoint = GetEndpointFromCompilation(compilation);
        var httpContext = CreateHttpContext();

        if (queryStringInput != null)
        {
            httpContext.Request.QueryString = new QueryString($"?p={UrlEncoder.Default.Encode(queryStringInput)}");
        }

        await endpoint.RequestDelegate(httpContext);
        Assert.Equal(200, httpContext.Response.StatusCode);
        Assert.Equal(expectedParameterValue, httpContext.Items["tryParsable"]);
    }

    [Theory]
    [MemberData(nameof(TryParsableParameters))]
    public async Task MapAction_TryParsableExplicitRouteParameters(string typeName, string routeValue, object expectedParameterValue)
    {
        var (results, compilation) = await RunGeneratorAsync($$"""
app.MapGet("/{routeValue}", (HttpContext context, [FromRoute]{{typeName}} routeValue) =>
{
    context.Items["tryParsable"] = routeValue;
});
""");
        var endpoint = GetEndpointFromCompilation(compilation);
        var httpContext = CreateHttpContext();
        httpContext.Request.RouteValues["routeValue"] = routeValue;

        await endpoint.RequestDelegate(httpContext);
        Assert.Equal(200, httpContext.Response.StatusCode);
        Assert.Equal(expectedParameterValue, httpContext.Items["tryParsable"]);
    }

    [Theory]
    [MemberData(nameof(TryParsableParameters))]
    public async Task MapAction_TryParsableExplicitHeaderParameters(string typeName, string headerValue, object expectedParameterValue)
    {
        var (results, compilation) = await RunGeneratorAsync($$"""
app.MapGet("/", (HttpContext context, [FromHeader]{{typeName}} headerValue) =>
{
    context.Items["tryParsable"] = headerValue;
});
""");
        var endpoint = GetEndpointFromCompilation(compilation);
        var httpContext = CreateHttpContext();
        httpContext.Request.Headers["headerValue"] = headerValue;

        await endpoint.RequestDelegate(httpContext);
        Assert.Equal(200, httpContext.Response.StatusCode);
        Assert.Equal(expectedParameterValue, httpContext.Items["tryParsable"]);
    }

    [Theory]
    [MemberData(nameof(TryParsableParameters))]
    public async Task MapAction_SingleParsable_StringReturn(string typeName, string queryStringInput, object expectedParameterValue)
    {
        var (results, compilation) = await RunGeneratorAsync($$"""
app.MapGet("/hello", (HttpContext context, [FromQuery]{{typeName}} p) =>
{
    context.Items["tryParsable"] = p;
});
""");
        var endpoint = GetEndpointFromCompilation(compilation);
        var httpContext = CreateHttpContext();

        if (queryStringInput != null)
        {
            httpContext.Request.QueryString = new QueryString($"?p={UrlEncoder.Default.Encode(queryStringInput)}");
        }

        await endpoint.RequestDelegate(httpContext);
        Assert.Equal(200, httpContext.Response.StatusCode);
        Assert.Equal(expectedParameterValue, httpContext.Items["tryParsable"]);
    }

    [Theory]
    [InlineData("PrecedenceCheckTodoWithoutFormat", "24")]
    [InlineData("PrecedenceCheckTodo", "42")]
    public async Task MapAction_TryParsePrecedenceCheck(string parameterType, string result)
    {
        var (results, compilation) = await RunGeneratorAsync($$"""
app.MapGet("/hello", ([FromQuery]{{parameterType}} p) => p.MagicValue);
""");
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(results, (endpointModel) =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
            var p = Assert.Single(endpointModel.Parameters);
            Assert.Equal(EndpointParameterSource.Query, p.Source);
            Assert.Equal("p", p.Name);
        });

        var httpContext = CreateHttpContext();
        httpContext.Request.QueryString = new QueryString("?p=1");

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, result);
    }

    [Fact]
    public async Task MapAction_SingleComplexTypeParam_StringReturn()
    {
        // HACK! Notice the return value of p.Name! - this is because TestMapActions.cs has #nullable enable
        // set and the compiler is returning when it is simply p.Name:
        //
        //     CS8603: Possible null reference return.
        //
        // Without source gen this same code isn't a problem.
        var (results, compilation) = await RunGeneratorAsync("""
app.MapGet("/hello", ([FromQuery] TryParseTodo p) => p.Name!);
""");
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(results, endpointModel =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
            var p = Assert.Single(endpointModel.Parameters);
            Assert.Equal(EndpointParameterSource.Query, p.Source);
            Assert.Equal("p", p.Name);
        });

        var httpContext = CreateHttpContext();
        httpContext.Request.QueryString = new QueryString("?p=1");

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, "Knit kitten mittens.");
        await VerifyAgainstBaselineUsingFile(compilation);
    }

    [Fact]
    public async Task MapAction_SingleEnumParam_StringReturn()
    {
        // HACK! Notice the return value of p.Name! - this is because TestMapActions.cs has #nullable enable
        // set and the compiler is returning when it is simply p.Name:
        //
        //     CS8603: Possible null reference return.
        //
        // Without source gen this same code isn't a problem.
        var (results, compilation) = await RunGeneratorAsync("""
app.MapGet("/hello", ([FromQuery]TodoStatus p) => p.ToString());
""");
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(results, endpointModel =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
            var p = Assert.Single(endpointModel.Parameters);
            Assert.Equal(EndpointParameterSource.Query, p.Source);
            Assert.Equal("p", p.Name);
        });

        var httpContext = CreateHttpContext();
        httpContext.Request.QueryString = new QueryString("?p=Done");

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, "Done");
        await VerifyAgainstBaselineUsingFile(compilation);
    }

    // [Fact]
    // public async Task MapAction_SingleNullableStringParam_WithQueryStringValueProvided_StringReturn()

    [Fact]
    public async Task MapAction_SingleNullableStringParam_WithEmptyQueryStringValueProvided_StringReturn()
    {
        var (results, compilation) = await RunGeneratorAsync("""
app.MapGet("/hello", ([FromQuery]string? p) => p == string.Empty ? "No value, but not null!" : "Was null!");
""");
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(results, endpointModel =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
            var p = Assert.Single(endpointModel.Parameters);
            Assert.Equal(EndpointParameterSource.Query, p.Source);
            Assert.Equal("p", p.Name);
        });

        var httpContext = CreateHttpContext();
        httpContext.Request.QueryString = new QueryString("?p=");

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, "No value, but not null!");
        await VerifyAgainstBaselineUsingFile(compilation);
    }

    [Fact]
    public async Task MapAction_MultipleStringParam_StringReturn()
    {
        var (results, compilation) = await RunGeneratorAsync("""
app.MapGet("/hello", ([FromQuery]string p1, [FromQuery]string p2) => $"{p1} {p2}");
""");
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(results, endpointModel =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
        });

        var httpContext = CreateHttpContext();
        httpContext.Request.QueryString = new QueryString("?p1=Hello&p2=world!");

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, "Hello world!");
        await VerifyAgainstBaselineUsingFile(compilation);
    }

    [Theory]
    [InlineData("HttpContext")]
    [InlineData("HttpRequest")]
    [InlineData("HttpResponse")]
    [InlineData("System.IO.Pipelines.PipeReader")]
    [InlineData("System.IO.Stream")]
    [InlineData("System.Security.Claims.ClaimsPrincipal")]
    [InlineData("System.Threading.CancellationToken")]
    public async Task MapAction_SingleSpecialTypeParam_StringReturn(string parameterType)
    {
        var (results, compilation) = await RunGeneratorAsync($"""
app.MapGet("/hello", ({parameterType} p) => p == null ? "null!" : "Hello world!");
""");
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(results, endpointModel =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
            var p = Assert.Single(endpointModel.Parameters);
            Assert.Equal(EndpointParameterSource.SpecialType, p.Source);
            Assert.Equal("p", p.Name);
        });

        var httpContext = CreateHttpContext();
        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, "Hello world!");
    }

    [Fact]
    public async Task MapAction_MultipleSpecialTypeParam_StringReturn()
    {
        var (results, compilation) = await RunGeneratorAsync("""
app.MapGet("/hello", (HttpRequest req, HttpResponse res) => req is null || res is null ? "null!" : "Hello world!");
""");
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(results, endpointModel =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);

            Assert.Collection(endpointModel.Parameters,
                reqParam =>
                {
                    Assert.Equal(EndpointParameterSource.SpecialType, reqParam.Source);
                    Assert.Equal("req", reqParam.Name);
                },
                reqParam =>
                {
                    Assert.Equal(EndpointParameterSource.SpecialType, reqParam.Source);
                    Assert.Equal("res", reqParam.Name);
                });
        });

        var httpContext = CreateHttpContext();
        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, "Hello world!");
        await VerifyAgainstBaselineUsingFile(compilation);
    }

    [Fact]
    public async Task MapAction_MultilineLambda()
    {
        var source = """
app.MapGet("/hello", () =>
{
    return "Hello world!";
});
""";
        var (result, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(result, endpointModel =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
        });

        var httpContext = CreateHttpContext();
        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, "Hello world!");
    }

    [Fact]
    public async Task MapAction_NoParam_StringReturn_WithFilter()
    {
        var source = """
app.MapGet("/hello", () => "Hello world!")
    .AddEndpointFilter(async (context, next) => {
        var result = await next(context);
        return $"Filtered: {result}";
    });
""";
        var expectedBody = "Filtered: Hello world!";
        var (result, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        await VerifyAgainstBaselineUsingFile(compilation);
        VerifyStaticEndpointModel(result, endpointModel =>
        {
            Assert.Equal("/hello", endpointModel.RoutePattern);
        });

        var httpContext = CreateHttpContext();
        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody);
    }

    [Theory]
    [InlineData(@"app.MapGet(""/"", () => 123456);", "123456")]
    [InlineData(@"app.MapGet(""/"", () => true);", "true")]
    [InlineData(@"app.MapGet(""/"", () => new DateTime(2023, 1, 1));", @"""2023-01-01T00:00:00""")]
    public async Task MapAction_NoParam_AnyReturn(string source, string expectedBody)
    {
        var (result, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(result, endpointModel =>
        {
            Assert.Equal("/", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
        });

        var httpContext = CreateHttpContext();
        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody);
    }

    public static IEnumerable<object[]> MapAction_NoParam_ComplexReturn_Data => new List<object[]>()
    {
        new object[] { """app.MapGet("/", () => new Todo() { Name = "Test Item"});""" },
        new object[] { """
object GetTodo() => new Todo() { Name = "Test Item"};
app.MapGet("/", GetTodo);
"""},
        new object[] { """app.MapGet("/", () => TypedResults.Ok(new Todo() { Name = "Test Item"}));""" },
        new object[] { """
Todo GetTodo() => new Todo() { Name = "Test Item"};
app.MapGet("/", GetTodo);
"""}
    };

    [Theory]
    [MemberData(nameof(MapAction_NoParam_ComplexReturn_Data))]
    public async Task MapAction_NoParam_ComplexReturn(string source)
    {
        var expectedBody = """{"id":0,"name":"Test Item","isComplete":false}""";
        var (result, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(result, endpointModel =>
        {
            Assert.Equal("/", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
        });

        var httpContext = CreateHttpContext();
        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody);
    }

    public static IEnumerable<object[]> MapAction_NoParam_TaskOfTReturn_Data => new List<object[]>()
    {
        new object[] { @"app.MapGet(""/"", () => Task.FromResult(""Hello world!""));", "Hello world!" },
        new object[] { @"app.MapGet(""/"", () => Task.FromResult(new Todo() { Name = ""Test Item"" }));", """{"id":0,"name":"Test Item","isComplete":false}""" },
        new object[] { @"app.MapGet(""/"", () => Task.FromResult(TypedResults.Ok(new Todo() { Name = ""Test Item"" })));", """{"id":0,"name":"Test Item","isComplete":false}""" }
    };

    [Theory]
    [MemberData(nameof(MapAction_NoParam_TaskOfTReturn_Data))]
    public async Task MapAction_NoParam_TaskOfTReturn(string source, string expectedBody)
    {
        var (result, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(result, endpointModel =>
        {
            Assert.Equal("/", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
            Assert.True(endpointModel.Response.IsAwaitable);
        });

        var httpContext = CreateHttpContext();
        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody);
    }

    public static IEnumerable<object[]> MapAction_NoParam_ValueTaskOfTReturn_Data => new List<object[]>()
    {
        new object[] { @"app.MapGet(""/"", () => ValueTask.FromResult(""Hello world!""));", "Hello world!" },
        new object[] { @"app.MapGet(""/"", () => ValueTask.FromResult(new Todo() { Name = ""Test Item""}));", """{"id":0,"name":"Test Item","isComplete":false}""" },
        new object[] { @"app.MapGet(""/"", () => ValueTask.FromResult(TypedResults.Ok(new Todo() { Name = ""Test Item""})));", """{"id":0,"name":"Test Item","isComplete":false}""" }
    };

    [Theory]
    [MemberData(nameof(MapAction_NoParam_ValueTaskOfTReturn_Data))]
    public async Task MapAction_NoParam_ValueTaskOfTReturn(string source, string expectedBody)
    {
        var (result, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(result, endpointModel =>
        {
            Assert.Equal("/", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
            Assert.True(endpointModel.Response.IsAwaitable);
        });

        var httpContext = CreateHttpContext();
        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody);
    }

    public static IEnumerable<object[]> MapAction_NoParam_TaskLikeOfObjectReturn_Data => new List<object[]>()
    {
        new object[] { @"app.MapGet(""/"", () => new ValueTask<object>(""Hello world!""));", "Hello world!" },
        new object[] { @"app.MapGet(""/"", () => Task<object>.FromResult(""Hello world!""));", "Hello world!" },
        new object[] { @"app.MapGet(""/"", () => new ValueTask<object>(new Todo() { Name = ""Test Item""}));", """{"id":0,"name":"Test Item","isComplete":false}""" },
        new object[] { @"app.MapGet(""/"", () => Task<object>.FromResult(new Todo() { Name = ""Test Item""}));", """{"id":0,"name":"Test Item","isComplete":false}""" },
        new object[] { @"app.MapGet(""/"", () => new ValueTask<object>(TypedResults.Ok(new Todo() { Name = ""Test Item""})));", """{"id":0,"name":"Test Item","isComplete":false}""" },
        new object[] { @"app.MapGet(""/"", () => Task<object>.FromResult(TypedResults.Ok(new Todo() { Name = ""Test Item""})));", """{"id":0,"name":"Test Item","isComplete":false}""" }
    };

    [Theory]
    [MemberData(nameof(MapAction_NoParam_TaskLikeOfObjectReturn_Data))]
    public async Task MapAction_NoParam_TaskLikeOfObjectReturn(string source, string expectedBody)
    {
        var (result, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        VerifyStaticEndpointModel(result, endpointModel =>
        {
            Assert.Equal("/", endpointModel.RoutePattern);
            Assert.Equal("MapGet", endpointModel.HttpMethod);
            Assert.True(endpointModel.Response.IsAwaitable);
        });

        var httpContext = CreateHttpContext();
        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody);
    }

    [Fact]
    public async Task Multiple_MapAction_NoParam_StringReturn()
    {
        var source = """
app.MapGet("/en", () => "Hello world!");
app.MapGet("/es", () => "Hola mundo!");
app.MapGet("/en-task", () => Task.FromResult("Hello world!"));
app.MapGet("/es-task", () => new ValueTask<string>("Hola mundo!"));
""";
        var (_, compilation) = await RunGeneratorAsync(source);

        await VerifyAgainstBaselineUsingFile(compilation);
    }

    [Fact]
    public async Task Multiple_MapAction_WithParams_StringReturn()
    {
        var source = """
app.MapGet("/en", (HttpRequest req) => "Hello world!");
app.MapGet("/es", (HttpResponse res) => "Hola mundo!");
app.MapGet("/zh", (HttpRequest req, HttpResponse res) => "你好世界！");
""";
        var (results, compilation) = await RunGeneratorAsync(source);
        var endpoints = GetEndpointsFromCompilation(compilation);

        await VerifyAgainstBaselineUsingFile(compilation);
        VerifyStaticEndpointModels(results, endpointModels => Assert.Collection(endpointModels,
            endpointModel =>
            {
                Assert.Equal("/en", endpointModel.RoutePattern);
                Assert.Equal("MapGet", endpointModel.HttpMethod);
                var reqParam = Assert.Single(endpointModel.Parameters);
                Assert.Equal(EndpointParameterSource.SpecialType, reqParam.Source);
                Assert.Equal("req", reqParam.Name);
            },
            endpointModel =>
            {
                Assert.Equal("/es", endpointModel.RoutePattern);
                Assert.Equal("MapGet", endpointModel.HttpMethod);
                var reqParam = Assert.Single(endpointModel.Parameters);
                Assert.Equal(EndpointParameterSource.SpecialType, reqParam.Source);
                Assert.Equal("res", reqParam.Name);
            },
            endpointModel =>
            {
                Assert.Equal("/zh", endpointModel.RoutePattern);
                Assert.Equal("MapGet", endpointModel.HttpMethod);
                Assert.Collection(endpointModel.Parameters,
                    reqParam =>
                    {
                        Assert.Equal(EndpointParameterSource.SpecialType, reqParam.Source);
                        Assert.Equal("req", reqParam.Name);
                    },
                    reqParam =>
                    {
                        Assert.Equal(EndpointParameterSource.SpecialType, reqParam.Source);
                        Assert.Equal("res", reqParam.Name);
                    });
            }));

        Assert.Equal(3, endpoints.Length);
        var httpContext = CreateHttpContext();
        await endpoints[0].RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, "Hello world!");

        httpContext = CreateHttpContext();
        await endpoints[1].RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, "Hola mundo!");

        httpContext = CreateHttpContext();
        await endpoints[2].RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, "你好世界！");
    }

    public static object[][] MapAction_ExplicitBodyParam_ComplexReturn_Data
    {
        get
        {
            var expectedBody = """{"id":0,"name":"Test Item","isComplete":false}""";
            var todo = new Todo()
            {
                Id = 0,
                Name = "Test Item",
                IsComplete = false
            };
            var withFilter = """
.AddEndpointFilter((c, n) => n(c));
""";
            var fromBodyRequiredSource = """app.MapPost("/", ([FromBody] Todo todo) => TypedResults.Ok(todo));""";
            var fromBodyEmptyBodyBehaviorSource = """app.MapPost("/", ([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] Todo todo) => TypedResults.Ok(todo));""";
            var fromBodyAllowEmptySource = """app.MapPost("/", ([CustomFromBody(AllowEmpty = true)] Todo todo) => TypedResults.Ok(todo));""";
            var fromBodyNullableSource = """app.MapPost("/", ([FromBody] Todo? todo) => TypedResults.Ok(todo));""";
            var fromBodyDefaultValueSource = """
#nullable disable
IResult postTodoWithDefault([FromBody] Todo todo = null) => TypedResults.Ok(todo);
app.MapPost("/", postTodoWithDefault);
#nullable restore
""";
            var fromBodyRequiredWithFilterSource = $"""app.MapPost("/", ([FromBody] Todo todo) => TypedResults.Ok(todo)){withFilter}""";
            var fromBodyEmptyBehaviorWithFilterSource = $"""app.MapPost("/", ([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] Todo todo) => TypedResults.Ok(todo)){withFilter}""";
            var fromBodyAllowEmptyWithFilterSource = $"""app.MapPost("/", ([CustomFromBody(AllowEmpty = true)] Todo todo) => TypedResults.Ok(todo)){withFilter}""";
            var fromBodyNullableWithFilterSource = $"""app.MapPost("/", ([FromBody] Todo?  todo) => TypedResults.Ok(todo)){withFilter}""";
            var fromBodyDefaultValueWithFilterSource = $"""
#nullable disable
IResult postTodoWithDefault([FromBody] Todo todo = null) => TypedResults.Ok(todo);
app.MapPost("/", postTodoWithDefault){withFilter}
#nullable restore
""";

            return new[]
            {
                new object[] { fromBodyRequiredSource, todo, 200, expectedBody },
                new object[] { fromBodyRequiredSource, null, 400, string.Empty },
                new object[] { fromBodyEmptyBodyBehaviorSource, todo, 200, expectedBody },
                new object[] { fromBodyEmptyBodyBehaviorSource, null, 200, string.Empty },
                new object[] { fromBodyAllowEmptySource, todo, 200, expectedBody },
                new object[] { fromBodyAllowEmptySource, null, 200, string.Empty },
                new object[] { fromBodyNullableSource, todo, 200, expectedBody },
                new object[] { fromBodyNullableSource, null, 200, string.Empty },
                new object[] { fromBodyDefaultValueSource, todo, 200, expectedBody },
                new object[] { fromBodyDefaultValueSource, null, 200, string.Empty },
                new object[] { fromBodyRequiredWithFilterSource, todo, 200, expectedBody },
                new object[] { fromBodyRequiredWithFilterSource, null, 400, string.Empty },
                new object[] { fromBodyEmptyBehaviorWithFilterSource, todo, 200, expectedBody },
                new object[] { fromBodyEmptyBehaviorWithFilterSource, null, 200, string.Empty },
                new object[] { fromBodyAllowEmptyWithFilterSource, todo, 200, expectedBody },
                new object[] { fromBodyAllowEmptyWithFilterSource, null, 200, string.Empty },
                new object[] { fromBodyNullableWithFilterSource, todo, 200, expectedBody },
                new object[] { fromBodyNullableWithFilterSource, null, 200, string.Empty },
                new object[] { fromBodyDefaultValueWithFilterSource, todo, 200, expectedBody },
                new object[] { fromBodyDefaultValueSource, null, 200, string.Empty },
            };
        }
    }

    [Theory]
    [MemberData(nameof(MapAction_ExplicitBodyParam_ComplexReturn_Data))]
    public async Task MapAction_ExplicitBodyParam_ComplexReturn(string source, Todo requestData, int expectedStatusCode, string expectedBody)
    {
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        var httpContext = CreateHttpContext();
        httpContext.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature(true));
        httpContext.Request.Headers["Content-Type"] = "application/json";

        var requestBodyBytes = JsonSerializer.SerializeToUtf8Bytes(requestData);
        var stream = new MemoryStream(requestBodyBytes);
        httpContext.Request.Body = stream;
        httpContext.Request.Headers["Content-Length"] = stream.Length.ToString(CultureInfo.InvariantCulture);

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody, expectedStatusCode);
    }

    [Fact]
    public async Task MapAction_ExplicitBodyParam_ComplexReturn_Snapshot()
    {
        var expectedBody = """{"id":0,"name":"Test Item","isComplete":false}""";
        var todo = new Todo()
        {
            Id = 0,
            Name = "Test Item",
            IsComplete = false
        };
        var source = $"""
app.MapPost("/fromBodyRequired", ([FromBody] Todo todo) => TypedResults.Ok(todo));
#pragma warning disable CS8622
app.MapPost("/fromBodyOptional", ([FromBody] Todo? todo) => TypedResults.Ok(todo));
#pragma warning restore CS8622
""";
        var (_, compilation) = await RunGeneratorAsync(source);

        await VerifyAgainstBaselineUsingFile(compilation);

        var endpoints = GetEndpointsFromCompilation(compilation);

        Assert.Equal(2, endpoints.Length);

        // formBodyRequired accepts a provided input
        var httpContext = CreateHttpContextWithBody(todo);
        await endpoints[0].RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody);

        // formBodyRequired throws on null input
        httpContext = CreateHttpContextWithBody(null);
        await endpoints[0].RequestDelegate(httpContext);
        Assert.Equal(400, httpContext.Response.StatusCode);

        // formBodyOptional accepts a provided input
        httpContext = CreateHttpContextWithBody(todo);
        await endpoints[1].RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody);

        // formBodyOptional accepts a null input
        httpContext = CreateHttpContextWithBody(null);
        await endpoints[1].RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, string.Empty);
    }

    public static object[][] MapAction_ExplicitHeaderParam_SimpleReturn_Data
    {
        get
        {
            var expectedBody = "Test header value";
            var fromHeaderRequiredSource = """app.MapGet("/", ([FromHeader] string headerValue) => headerValue);""";
            var fromHeaderWithNameRequiredSource = """app.MapGet("/", ([FromHeader(Name = "headerValue")] string parameterName) => parameterName);""";
            var fromHeaderWithNullNameRequiredSource = """app.MapGet("/", ([FromHeader(Name = null)] string headerValue) => headerValue);""";
            var fromHeaderNullableSource = """app.MapGet("/", ([FromHeader] string? headerValue) => headerValue ?? string.Empty);""";
            var fromHeaderDefaultValueSource = """
#nullable disable
string getHeaderWithDefault([FromHeader] string headerValue = null) => headerValue ?? string.Empty;
app.MapGet("/", getHeaderWithDefault);
#nullable restore
""";

            return new[]
            {
                new object[] { fromHeaderRequiredSource, expectedBody, 200, expectedBody },
                new object[] { fromHeaderRequiredSource, null, 400, string.Empty },
                new object[] { fromHeaderWithNameRequiredSource, expectedBody, 200, expectedBody },
                new object[] { fromHeaderWithNameRequiredSource, null, 400, string.Empty },
                new object[] { fromHeaderWithNullNameRequiredSource, expectedBody, 200, expectedBody },
                new object[] { fromHeaderWithNullNameRequiredSource, null, 400, string.Empty },
                new object[] { fromHeaderNullableSource, expectedBody, 200, expectedBody },
                new object[] { fromHeaderNullableSource, null, 200, string.Empty },
                new object[] { fromHeaderDefaultValueSource, expectedBody, 200, expectedBody },
                new object[] { fromHeaderDefaultValueSource, null, 200, string.Empty },
            };
        }
    }

    [Theory]
    [MemberData(nameof(MapAction_ExplicitHeaderParam_SimpleReturn_Data))]
    public async Task MapAction_ExplicitHeaderParam_SimpleReturn(string source, string requestData, int expectedStatusCode, string expectedBody)
    {
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        var httpContext = CreateHttpContext();
        if (requestData is not null)
        {
            httpContext.Request.Headers["headerValue"] = requestData;
        }

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody, expectedStatusCode);
    }

    public static object[][] MapAction_ExplicitRouteParam_SimpleReturn_Data
    {
        get
        {
            var expectedBody = "Test route value";
            var fromRouteRequiredSource = """app.MapGet("/{routeValue}", ([FromRoute] string routeValue) => routeValue);""";
            var fromRouteWithNameRequiredSource = """app.MapGet("/{routeValue}", ([FromRoute(Name = "routeValue" )] string parameterName) => parameterName);""";
            var fromRouteWithNullNameRequiredSource = """app.MapGet("/{routeValue}", ([FromRoute(Name = null )] string routeValue) => routeValue);""";
            var fromRouteNullableSource = """app.MapGet("/{routeValue}", ([FromRoute] string? routeValue) => routeValue ?? string.Empty);""";
            var fromRouteDefaultValueSource = """
#nullable disable
string getRouteWithDefault([FromRoute] string routeValue = null) => routeValue ?? string.Empty;
app.MapGet("/{routeValue}", getRouteWithDefault);
#nullable restore
""";

            return new[]
            {
                new object[] { fromRouteRequiredSource, expectedBody, 200, expectedBody },
                new object[] { fromRouteRequiredSource, null, 400, string.Empty },
                new object[] { fromRouteWithNameRequiredSource, expectedBody, 200, expectedBody },
                new object[] { fromRouteWithNameRequiredSource, null, 400, string.Empty },
                new object[] { fromRouteWithNullNameRequiredSource, expectedBody, 200, expectedBody },
                new object[] { fromRouteWithNullNameRequiredSource, null, 400, string.Empty },
                new object[] { fromRouteNullableSource, expectedBody, 200, expectedBody },
                new object[] { fromRouteNullableSource, null, 200, string.Empty },
                new object[] { fromRouteDefaultValueSource, expectedBody, 200, expectedBody },
                new object[] { fromRouteDefaultValueSource, null, 200, string.Empty },
            };
        }
    }

    [Theory]
    [MemberData(nameof(MapAction_ExplicitRouteParam_SimpleReturn_Data))]
    public async Task MapAction_ExplicitRouteParam_SimpleReturn(string source, string requestData, int expectedStatusCode, string expectedBody)
    {
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        var httpContext = CreateHttpContext();
        if (requestData is not null)
        {
            httpContext.Request.RouteValues["routeValue"] = requestData;
        }

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody, expectedStatusCode);
    }

    public static object[][] MapAction_RouteOrQueryParam_SimpleReturn_Data
    {
        get
        {
            var expectedBody = "ValueFromRouteOrQuery";
            var implicitRouteRequiredSource = """app.MapGet("/{value}", (string value) => value);""";
            var implicitQueryRequiredSource = """app.MapGet("", (string value) => value);""";
            var implicitRouteNullableSource = """app.MapGet("/{value}", (string? value) => value ?? string.Empty);""";
            var implicitQueryNullableSource = """app.MapGet("/", (string? value) => value ?? string.Empty);""";
            var implicitRouteDefaultValueSource = """
#nullable disable
string getRouteWithDefault(string value = null) => value ?? string.Empty;
app.MapGet("/{value}", getRouteWithDefault);
#nullable restore
""";

            var implicitQueryDefaultValueSource = """
#nullable disable
string getQueryWithDefault(string value = null) => value ?? string.Empty;
app.MapGet("/", getQueryWithDefault);
#nullable restore
""";

            return new[]
            {
                new object[] { implicitRouteRequiredSource, true, false, 200, expectedBody },
                new object[] { implicitRouteRequiredSource, false, false, 400, string.Empty },
                new object[] { implicitQueryRequiredSource, false, true, 200, expectedBody },
                new object[] { implicitQueryRequiredSource, false, false, 400, string.Empty },

                new object[] { implicitRouteNullableSource, true, false, 200, expectedBody },
                new object[] { implicitRouteNullableSource, false, false, 200, string.Empty },
                new object[] { implicitQueryNullableSource, false, true, 200, expectedBody },
                new object[] { implicitQueryNullableSource, false, false, 200, string.Empty },

                new object[] { implicitRouteDefaultValueSource, true, false, 200, expectedBody },
                new object[] { implicitRouteDefaultValueSource, false, false, 200, string.Empty },
                new object[] { implicitQueryDefaultValueSource, false, true, 200, expectedBody },
                new object[] { implicitQueryDefaultValueSource, false, false, 200, string.Empty },
            };
        }
    }

    [Theory]
    [MemberData(nameof(MapAction_RouteOrQueryParam_SimpleReturn_Data))]
    public async Task MapAction_RouteOrQueryParam_SimpleReturn(string source, bool hasRoute, bool hasQuery, int expectedStatusCode, string expectedBody)
    {
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        var httpContext = CreateHttpContext();
        if (hasRoute)
        {
            httpContext.Request.RouteValues["value"] = expectedBody;
        }

        if (hasQuery)
        {
            httpContext.Request.QueryString = new QueryString($"?value={expectedBody}");
        }

        await endpoint.RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody, expectedStatusCode);
    }

    public static object[][] MapAction_ExplicitServiceParam_SimpleReturn_Data
    {
        get
        {
            var fromServiceRequiredSource = """app.MapPost("/", ([FromServices]TestService svc) => svc.TestServiceMethod());""";
            var fromServiceNullableSource = """app.MapPost("/", ([FromServices]TestService? svc) => svc?.TestServiceMethod() ?? string.Empty);""";
            var fromServiceDefaultValueSource = """
#nullable disable
string postServiceWithDefault([FromServices]TestService svc = null) => svc?.TestServiceMethod() ?? string.Empty;
app.MapPost("/", postServiceWithDefault);
#nullable restore
""";

            var fromServiceEnumerableRequiredSource = """app.MapPost("/", ([FromServices]IEnumerable<TestService>  svc) => svc.FirstOrDefault()?.TestServiceMethod() ?? string.Empty);""";
            var fromServiceEnumerableNullableSource = """app.MapPost("/", ([FromServices]IEnumerable<TestService>? svc) => svc?.FirstOrDefault()?.TestServiceMethod() ?? string.Empty);""";
            var fromServiceEnumerableDefaultValueSource = """
#nullable disable
string postServiceWithDefault([FromServices]IEnumerable<TestService> svc = null) => svc?.FirstOrDefault()?.TestServiceMethod() ?? string.Empty;
app.MapPost("/", postServiceWithDefault);
#nullable restore
""";

            return new[]
            {
                new object[] { fromServiceRequiredSource, true, true },
                new object[] { fromServiceRequiredSource, false, false },
                new object[] { fromServiceNullableSource, true, true },
                new object[] { fromServiceNullableSource, false, true },
                new object[] { fromServiceDefaultValueSource, true, true },
                new object[] { fromServiceDefaultValueSource, false, true },
                new object[] { fromServiceEnumerableRequiredSource, true, true },
                new object[] { fromServiceEnumerableRequiredSource, false, true },
                new object[] { fromServiceEnumerableNullableSource, true, true },
                new object[] { fromServiceEnumerableNullableSource, false, true },
                new object[] { fromServiceEnumerableDefaultValueSource, true, true },
                new object[] { fromServiceEnumerableDefaultValueSource, false, true }
            };
        }
    }

    [Theory]
    [MemberData(nameof(MapAction_ExplicitServiceParam_SimpleReturn_Data))]
    public async Task MapAction_ExplicitServiceParam_SimpleReturn(string source, bool hasService, bool isValid)
    {
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        var httpContext = CreateHttpContext();
        if (hasService)
        {
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton<TestService>(new TestService());
            var services = serviceCollection.BuildServiceProvider();
            httpContext.RequestServices = services;
        }

        if (isValid)
        {
            await endpoint.RequestDelegate(httpContext);
            await VerifyResponseBodyAsync(httpContext, hasService ? "Produced from service!" : string.Empty);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => endpoint.RequestDelegate(httpContext));
            Assert.False(httpContext.RequestAborted.IsCancellationRequested);
        }
    }

    [Fact]
    public async Task MapAction_ExplicitServiceParam_SimpleReturn_Snapshot()
    {
        var source = """
app.MapGet("/fromServiceRequired", ([FromServices]TestService svc) => svc.TestServiceMethod());
app.MapGet("/enumerableFromService", ([FromServices]IEnumerable<TestService> svc) => svc?.FirstOrDefault()?.TestServiceMethod() ?? string.Empty);
app.MapGet("/multipleFromService", ([FromServices]TestService? svc, [FromServices]IEnumerable<TestService> svcs) =>
    $"{(svcs?.FirstOrDefault()?.TestServiceMethod() ?? string.Empty)}, {svc?.TestServiceMethod()}");
""";
        var httpContext = CreateHttpContext();
        var expectedBody = "Produced from service!";
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<TestService>(new TestService());
        var services = serviceCollection.BuildServiceProvider();
        var emptyServices = new ServiceCollection().BuildServiceProvider();

        var (_, compilation) = await RunGeneratorAsync(source);

        await VerifyAgainstBaselineUsingFile(compilation);

        var endpoints = GetEndpointsFromCompilation(compilation);

        Assert.Equal(3, endpoints.Length);

        // fromServiceRequired throws on null input
        httpContext.RequestServices = emptyServices;
        await Assert.ThrowsAsync<InvalidOperationException>(() => endpoints[0].RequestDelegate(httpContext));
        Assert.False(httpContext.RequestAborted.IsCancellationRequested);

        // fromServiceRequired accepts a provided input
        httpContext = CreateHttpContext();
        httpContext.RequestServices = services;
        await endpoints[0].RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody);

        // enumerableFromService
        httpContext = CreateHttpContext();
        httpContext.RequestServices = services;
        await endpoints[1].RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, expectedBody);

        // multipleFromService
        httpContext = CreateHttpContext();
        httpContext.RequestServices = services;
        await endpoints[2].RequestDelegate(httpContext);
        await VerifyResponseBodyAsync(httpContext, $"{expectedBody}, {expectedBody}");
    }

    [Fact]
    public async Task MapAction_ExplicitSource_SimpleReturn_Snapshot()
    {
        var source = """
app.MapGet("/fromQuery", ([FromQuery] string queryValue) => queryValue ?? string.Empty);
app.MapGet("/fromHeader", ([FromHeader] string headerValue) => headerValue ?? string.Empty);
app.MapGet("/fromRoute/{routeValue}", ([FromRoute] string routeValue) => routeValue ?? string.Empty);
app.MapGet("/fromRouteRequiredImplicit/{value}", (string value) => value);
app.MapGet("/fromQueryRequiredImplicit", (string value) => value);
""";
        var (_, compilation) = await RunGeneratorAsync(source);

        await VerifyAgainstBaselineUsingFile(compilation);
    }

    public static object[][] CanApplyFiltersOnHandlerWithVariousArguments_Data
    {
        get
        {
            var tooManyArguments = """
string HelloName([FromQuery] int? one, [FromQuery] string? two, [FromQuery] int? three, [FromQuery] string? four,
    [FromQuery] int? five, [FromQuery] bool? six, [FromQuery] string? seven, [FromQuery] string? eight,
    [FromQuery] int? nine, [FromQuery] string? ten, [FromQuery] int? eleven) =>
    "Too many arguments";
""";
            var noArguments = """
string HelloName() => "No arguments";
""";
            var justRightArguments = """
string HelloName([FromQuery] int? one, [FromQuery] string? two, [FromQuery] int? three, [FromQuery] string? four,
    [FromQuery] int? five, [FromQuery] bool? six, [FromQuery] string? seven) =>
    "Just right arguments";
""";
            return new object[][]
            {
                new [] { tooManyArguments, "True, 11, Too many arguments" },
                new [] { noArguments, "True, 0, No arguments" },
                new [] { justRightArguments, "False, 7, Just right arguments" },
            };
        }
    }

    [Theory]
    [MemberData(nameof(CanApplyFiltersOnHandlerWithVariousArguments_Data))]
    public async Task CanApplyFiltersOnHandlerWithVariousArguments(string handlerMethod, string expectedBody)
    {
        var source = $$"""
{{handlerMethod}}
app.MapGet("/", HelloName)
    .AddEndpointFilter(async (c, n) =>
    {
        var result = await n(c);
        return $"{(c is DefaultEndpointFilterInvocationContext)}, {c.Arguments.Count}, {result}";
    });
""";
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);
        var httpContext = CreateHttpContext();

        await endpoint.RequestDelegate(httpContext);

        await VerifyResponseBodyAsync(httpContext, expectedBody);
    }

    [Fact]
    public async Task MapAction_InferredTryParse_NonOptional_Provided()
    {
        var source = """
app.MapGet("/", (HttpContext httpContext, int id) =>
{
    httpContext.Items["id"] = id;
});
""";
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        var httpContext = CreateHttpContext();
        httpContext.Request.Query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["id"] = "42",
        });

        httpContext.Request.Headers.Referer = "https://example.org";
        await endpoint.RequestDelegate(httpContext);

        Assert.Equal(42, httpContext.Items["id"]);
        Assert.Equal(200, httpContext.Response.StatusCode);
    }

    public static object[][] BindAsyncUriTypesAndOptionalitySupport = new object[][]
    {
        new object[] { "MyBindAsyncRecord", false },
        new object[] { "MyBindAsyncStruct", true },
        new object[] { "MyNullableBindAsyncStruct", false },
        new object[] { "MyBothBindAsyncStruct", true },
        new object[] { "MySimpleBindAsyncRecord", false, },
        new object[] { "MySimpleBindAsyncStruct", true },
        new object[] { "BindAsyncFromImplicitStaticAbstractInterface", false },
        new object[] { "InheritBindAsync", false },
        new object[] { "BindAsyncFromExplicitStaticAbstractInterface", false },
        // TODO: Fix this
        //new object[] { "MyBindAsyncFromInterfaceRecord", false },
    };

    public static IEnumerable<object[]> BindAsyncUriTypes =>
        BindAsyncUriTypesAndOptionalitySupport.Select(x => new[] { x[0] });

    [Theory]
    [MemberData(nameof(BindAsyncUriTypes))]
    public async Task MapAction_BindAsync_Optional_Provided(string bindAsyncType)
    {
        var source = $$"""
app.MapGet("/", (HttpContext httpContext, {{bindAsyncType}}? myBindAsyncParam) =>
{
    httpContext.Items["uri"] = myBindAsyncParam?.Uri;
});
""";
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        var httpContext = CreateHttpContext();
        httpContext.Request.Headers.Referer = "https://example.org";
        await endpoint.RequestDelegate(httpContext);

        Assert.Equal(new Uri("https://example.org"), httpContext.Items["uri"]);
        Assert.Equal(200, httpContext.Response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(BindAsyncUriTypes))]
    public async Task MapAction_BindAsync_NonOptional_Provided(string bindAsyncType)
    {
        var source = $$"""
app.MapGet("/", (HttpContext httpContext, {{bindAsyncType}} myBindAsyncParam) =>
{
    httpContext.Items["uri"] = myBindAsyncParam.Uri;
});
""";
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        var httpContext = CreateHttpContext();
        httpContext.Request.Headers.Referer = "https://example.org";
        await endpoint.RequestDelegate(httpContext);

        Assert.Equal(new Uri("https://example.org"), httpContext.Items["uri"]);
        Assert.Equal(200, httpContext.Response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(BindAsyncUriTypesAndOptionalitySupport))]
    public async Task MapAction_BindAsync_Optional_NotProvided(string bindAsyncType, bool expectException)
    {
        var source = $$"""
app.MapGet("/", (HttpContext httpContext, {{bindAsyncType}}? myBindAsyncParam) =>
{
    httpContext.Items["uri"] = myBindAsyncParam?.Uri;
});
""";
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        var httpContext = CreateHttpContext();

        if (expectException)
        {
            // These types simply don't support optional parameters since they cannot return null.
            var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => endpoint.RequestDelegate(httpContext));
            Assert.Equal("The request is missing the required Referer header.", ex.Message);
        }
        else
        {
            await endpoint.RequestDelegate(httpContext);

            Assert.Null(httpContext.Items["uri"]);
            Assert.Equal(200, httpContext.Response.StatusCode);
        }
    }

    [Theory]
    [MemberData(nameof(BindAsyncUriTypesAndOptionalitySupport))]
    public async Task MapAction_BindAsync_NonOptional_NotProvided(string bindAsyncType, bool expectException)
    {
        var source = $$"""
app.MapGet("/", (HttpContext httpContext, {{bindAsyncType}} myBindAsyncParam) =>
{
    httpContext.Items["uri"] = myBindAsyncParam.Uri;
});
""";
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        var httpContext = CreateHttpContext();

        if (expectException)
        {
            var ex = await Assert.ThrowsAsync<BadHttpRequestException>(() => endpoint.RequestDelegate(httpContext));
            Assert.Equal("The request is missing the required Referer header.", ex.Message);
        }
        else
        {
            await endpoint.RequestDelegate(httpContext);

            Assert.Null(httpContext.Items["uri"]);
            Assert.Equal(400, httpContext.Response.StatusCode);
        }
    }

    [Fact]
    public async Task MapAction_BindAsync_Snapshot()
    {
        var source = new StringBuilder();

        var i = 0;
        while (i < BindAsyncUriTypesAndOptionalitySupport.Length * 2)
        {
            var bindAsyncType = BindAsyncUriTypesAndOptionalitySupport[i / 2][0];
            source.AppendLine(CultureInfo.InvariantCulture, $$"""app.MapGet("/{{i}}", (HttpContext httpContext, {{bindAsyncType}} myBindAsyncParam) => "Hello world! {{i}}");""");
            i++;
            source.AppendLine(CultureInfo.InvariantCulture, $$"""app.MapGet("/{{i}}", ({{bindAsyncType}}? myBindAsyncParam) => "Hello world! {{i}}");""");
            i++;
        }

        var (_, compilation) = await RunGeneratorAsync(source.ToString());

        await VerifyAgainstBaselineUsingFile(compilation);

        var endpoints = GetEndpointsFromCompilation(compilation);
        Assert.Equal(BindAsyncUriTypesAndOptionalitySupport.Length * 2, endpoints.Length);

        for (i = 0; i < BindAsyncUriTypesAndOptionalitySupport.Length * 2; i++)
        {
            var httpContext = CreateHttpContext();
            // Set a referrer header so BindAsync always succeeds and the route handler is always called optional or not.
            httpContext.Request.Headers.Referer = "https://example.org";

            await endpoints[i].RequestDelegate(httpContext);
            await VerifyResponseBodyAsync(httpContext, $"Hello world! {i}");
        }
    }

    [Fact]
    public async Task MapAction_BindAsync_ExceptionsAreUncaught()
    {
        var source = """
app.MapGet("/", (HttpContext httpContext, MyBindAsyncTypeThatThrows myBindAsyncParam) => { });
""";
        var (_, compilation) = await RunGeneratorAsync(source);
        var endpoint = GetEndpointFromCompilation(compilation);

        var httpContext = CreateHttpContext();
        httpContext.Request.Headers.Referer = "https://example.org";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => endpoint.RequestDelegate(httpContext));
        Assert.Equal("BindAsync failed", ex.Message);
    }

    public static object[][] MapAction_JsonBodyOrService_SimpleReturn_Data
    {
        get
        {
            var todo = new Todo()
            {
                Id = 0,
                Name = "Test Item",
                IsComplete = false
            };
            var expectedTodoBody = "Test Item";
            var expectedServiceBody = "Produced from service!";
            var implicitRequiredServiceSource = $"""app.MapPost("/", ({typeof(TestService)} svc) => svc.TestServiceMethod());""";
            var implicitRequiredJsonBodySource = $"""app.MapPost("/", (Todo todo) => todo.Name ?? string.Empty);""";

            return new[]
            {
                new object[] { implicitRequiredServiceSource, false, null, true, 200, expectedServiceBody },
                new object[] { implicitRequiredServiceSource, false, null, false, 400, string.Empty },
                new object[] { implicitRequiredJsonBodySource, true, todo, false, 200, expectedTodoBody },
                new object[] { implicitRequiredJsonBodySource, true, null, false, 400, string.Empty },
            };
        }
    }

    [Theory]
    [MemberData(nameof(MapAction_JsonBodyOrService_SimpleReturn_Data))]
    public async Task MapAction_JsonBodyOrService_SimpleReturn(string source, bool hasBody, Todo requestData, bool hasService, int expectedStatusCode, string expectedBody)
    {
        var (_, compilation) = await RunGeneratorAsync(source);
        var serviceProvider = CreateServiceProvider(hasService ?
            (serviceCollection) => serviceCollection.AddSingleton(new TestService())
            : null);
        var endpoint = GetEndpointFromCompilation(compilation, serviceProvider: serviceProvider);

        var httpContext = CreateHttpContext(serviceProvider);

        if (hasBody)
        {
            httpContext = CreateHttpContextWithBody(requestData);
        }

        await endpoint.RequestDelegate(httpContext);
        Console.WriteLine(expectedBody, expectedStatusCode);
        // await VerifyResponseBodyAsync(httpContext, expectedBody, expectedStatusCode);

    }
}
