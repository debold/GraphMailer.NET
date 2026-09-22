using System.Net;
using System.Text;
using GraphMailer.Service.Services;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace GraphMailer.Tests.Unit.Services;

/// <summary>
/// How <see cref="GraphApiClient"/> reads and classifies Exchange Online throttling: the
/// throttling classifier, the error reader for both shapes a Graph failure arrives in (mapped
/// <see cref="ODataError"/> and the retry handler's <see cref="AggregateException"/>), and the
/// retry policy for mailbox writes, exercised against the SDK's real <see cref="RetryHandler"/>.
/// The response bodies have the shape Exchange Online returns for a throttled mailbox.
/// </summary>
public sealed class GraphApiClientThrottlingTests
{
    private const string DirectoryConcurrencyBody =
        """{"error":{"code":"ErrorDirectoryConcurrencyLimit","message":"Directory Concurrency Limit Reached"}}""";

    private const string GatewayTimeoutBody =
        """{"error":{"code":"UnknownError","message":"","innerError":{"date":"2026-01-01T12:00:00","request-id":"11111111-2222-4333-8444-555555555555","client-request-id":"66666666-7777-4888-9999-aaaaaaaaaaaa"}}}""";

    // =========================================================================
    // IsThrottlingRejection
    // =========================================================================

    [Theory]
    [InlineData(503, "ErrorDirectoryConcurrencyLimit")]
    [InlineData(503, "CommandConcurrencyLimitReached")]
    [InlineData(504, "UnknownError")]
    [InlineData(429, "ApplicationThrottled")]
    [InlineData(503, "ServiceUnavailable")]
    [InlineData(500, "ErrorServerBusy")]                  // code-based regardless of status
    [InlineData(0, "MailboxConcurrency")]
    public void IsThrottlingRejection_BusyMailboxOrService_ReturnsTrue(int status, string code)
        => GraphApiClient.IsThrottlingRejection(status, code).Should().BeTrue();

    [Theory]
    [InlineData(500, "InternalServerError")]
    [InlineData(400, "BadRequest")]
    [InlineData(403, "ErrorSendAsDenied")]
    [InlineData(404, "ErrorInvalidUser")]
    [InlineData(401, "InvalidAuthenticationToken")]
    public void IsThrottlingRejection_OtherFailures_ReturnsFalse(int status, string code)
        => GraphApiClient.IsThrottlingRejection(status, code).Should().BeFalse();

    // =========================================================================
    // TryReadApiFailure
    // =========================================================================

    [Fact]
    public async Task TryReadApiFailure_RetryHandlerGaveUp_ReadsTheLastAttemptFromTheAggregate()
    {
        // Regression: the retry handler throws an AggregateException before any
        // error mapping, so the ODataError-only catch never saw it — no code, no request id,
        // and the queue stored the multi-line aggregate text as LastError.
        var responses = new Queue<HttpResponseMessage>(
        [
            Response(HttpStatusCode.GatewayTimeout, GatewayTimeoutBody),
            Response(HttpStatusCode.ServiceUnavailable, DirectoryConcurrencyBody),
            Response(HttpStatusCode.ServiceUnavailable, DirectoryConcurrencyBody),
            Response(HttpStatusCode.ServiceUnavailable, DirectoryConcurrencyBody),
        ]);
        var aggregate = await CaptureAsync(new RetryHandlerOption(), responses);

        aggregate.Should().BeOfType<AggregateException>("this is the shape the SDK throws after its retries");
        GraphApiClient.TryReadApiFailure(aggregate!, out var failure).Should().BeTrue();

        failure.Status.Should().Be(503);
        failure.Code.Should().Be("ErrorDirectoryConcurrencyLimit");
        failure.Message.Should().Be("Directory Concurrency Limit Reached");
        GraphApiClient.IsThrottlingRejection(failure.Status, failure.Code).Should().BeTrue();
        GraphApiClient.IsPermanentRejection(failure.Status, failure.Code).Should().BeFalse();
    }

    [Fact]
    public async Task TryReadApiFailure_GatewayTimeoutBody_ReadsRequestIdFromInnerError()
    {
        var responses = new Queue<HttpResponseMessage>(
            Enumerable.Range(0, 4).Select(_ => Response(HttpStatusCode.GatewayTimeout, GatewayTimeoutBody)));
        var aggregate = await CaptureAsync(new RetryHandlerOption(), responses);

        GraphApiClient.TryReadApiFailure(aggregate!, out var failure).Should().BeTrue();

        failure.Status.Should().Be(504);
        failure.Code.Should().Be("UnknownError");
        failure.RequestId.Should().Be("11111111-2222-4333-8444-555555555555");
    }

    [Fact]
    public void TryReadApiFailure_MappedODataError_ReadsItsFields()
    {
        var error = new ODataError
        {
            ResponseStatusCode = 503,
            Error = new MainError
            {
                Code = "CommandConcurrencyLimitReached",
                Message = "busy",
                InnerError = new InnerError { RequestId = "req-1" },
            },
        };

        GraphApiClient.TryReadApiFailure(error, out var failure).Should().BeTrue();

        failure.Should().Be(new GraphApiClient.ApiFailure(503, "CommandConcurrencyLimitReached", "busy", "req-1"));
    }

    [Fact]
    public void TryReadApiFailure_NonHttpFailure_ReturnsFalse()
    {
        GraphApiClient.TryReadApiFailure(new HttpRequestException("network down"), out _).Should().BeFalse();
        GraphApiClient.TryReadApiFailure(new AggregateException(new IOException("x")), out _).Should().BeFalse();
    }

    // =========================================================================
    // Mailbox-write retry policy
    // =========================================================================

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.GatewayTimeout, false)]
    public void ShouldRetryMailboxWrite_OnlyGraphThrottlingIsRetriedInPlace(HttpStatusCode status, bool expected)
        => GraphApiClient.ShouldRetryMailboxWrite(status).Should().Be(expected);

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task MailboxWriteRetryOption_BusyMailboxOrTimeout_IsNotResentByTheSdk(HttpStatusCode status)
    {
        // 504 may already have been sent behind the gateway (duplicate risk); 503 is a saturated
        // mailbox. Both go back to the queue after exactly one request.
        var stub = new StubHandler(new Queue<HttpResponseMessage>(
            Enumerable.Range(0, 4).Select(_ => Response(status, DirectoryConcurrencyBody))));
        using var http = new HttpClient(new RetryHandler(GraphApiClient.MailboxWriteRetryOption()) { InnerHandler = stub });

        var response = await http.PostAsync("https://graph.test/users/x/sendMail", new StringContent("{}"));

        response.StatusCode.Should().Be(status);
        stub.Calls.Should().Be(1);
    }

    [Fact]
    public async Task MailboxWriteRetryOption_GraphThrottling_IsStillRetriedInPlace()
    {
        var stub = new StubHandler(new Queue<HttpResponseMessage>(
        [
            Response(HttpStatusCode.TooManyRequests, """{"error":{"code":"ApplicationThrottled","message":"x"}}"""),
            new HttpResponseMessage(HttpStatusCode.Accepted),
        ]));
        using var http = new HttpClient(new RetryHandler(GraphApiClient.MailboxWriteRetryOption()) { InnerHandler = stub });

        var response = await http.PostAsync("https://graph.test/users/x/sendMail", new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        stub.Calls.Should().Be(2);
    }

    [Fact]
    public async Task MailboxWriteRetryOption_ThroughGraphServiceClient_SendsOnceAndSurfacesODataError()
    {
        // End to end through the SDK's default middleware pipeline: the per-request option must
        // override the client-wide retry handler, and the unretried 503 then arrives as a mapped
        // ODataError the classifier can read.
        var stub = new StubHandler(new Queue<HttpResponseMessage>(
            Enumerable.Range(0, 4).Select(_ => Response(HttpStatusCode.ServiceUnavailable, DirectoryConcurrencyBody))));
        using var http = Microsoft.Graph.GraphClientFactory.Create(
            Microsoft.Graph.GraphClientFactory.CreateDefaultHandlers(), finalHandler: stub);
        var client = new Microsoft.Graph.GraphServiceClient(
            http, new Microsoft.Kiota.Abstractions.Authentication.AnonymousAuthenticationProvider());

        var send = () => client.Users["busy@example.com"].SendMail.PostAsync(
            new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody(),
            rc => rc.Options.Add(GraphApiClient.MailboxWriteRetryOption()));

        var thrown = (await send.Should().ThrowAsync<ODataError>()).Which;
        stub.Calls.Should().Be(1);
        GraphApiClient.TryReadApiFailure(thrown, out var failure).Should().BeTrue();
        failure.Code.Should().Be("ErrorDirectoryConcurrencyLimit");
        GraphApiClient.IsThrottlingRejection(failure.Status, failure.Code).Should().BeTrue();
    }

    // =========================================================================
    // Diagnostic response headers & client-request-id
    // =========================================================================

    private const string AgsDiagnostic =
        """{"ServerInfo":{"DataCenter":"Test Region","Slice":"E","Ring":"1","ScaleUnit":"000","RoleInstance":"TEST0000000001"}}""";

    private static HttpResponseMessage WithDiagnosticHeaders(HttpResponseMessage response)
    {
        response.Headers.Remove("Retry-After");
        response.Headers.Add("Retry-After", "30");
        response.Headers.Add("client-request-id", "3f2a6b1c-0d4e-4f5a-9b8c-7d6e5f4a3b2c");
        response.Headers.Add("x-ms-ags-diagnostic", AgsDiagnostic);
        response.Headers.Date = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        return response;
    }

    [Fact]
    public async Task TryReadApiFailure_MappedODataError_ReadsDiagnosticResponseHeaders()
    {
        var stub = new StubHandler(new Queue<HttpResponseMessage>(
            [WithDiagnosticHeaders(Response(HttpStatusCode.ServiceUnavailable, DirectoryConcurrencyBody))]));
        var client = GraphClient(stub);

        var send = () => client.Users["busy@example.com"].SendMail.PostAsync(
            new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody(),
            GraphApiClient.DeliveryRequest("3f2a6b1c0d4e4f5a9b8c7d6e5f4a3b2c"));

        var thrown = (await send.Should().ThrowAsync<ODataError>()).Which;
        GraphApiClient.TryReadApiFailure(thrown, out var failure).Should().BeTrue();

        failure.RetryAfter.Should().Be("30");
        failure.ClientRequestId.Should().Be("3f2a6b1c-0d4e-4f5a-9b8c-7d6e5f4a3b2c");
        failure.AgsDiagnostic.Should().Be(AgsDiagnostic);
        failure.ServerDate.Should().Contain("01 Jan 2026 12:00:00");
        failure.DescribeHeaders().Should().Contain("Retry-After=30").And.Contain("x-ms-ags-diagnostic=");
    }

    [Fact]
    public async Task TryReadApiFailure_RetryHandlerGaveUp_ReadsDiagnosticHeadersOfTheLastAttempt()
    {
        // The shape a 429 takes once the SDK's in-place retries are used up.
        // Retry-After stays 0 here: the SDK really waits for it before giving up.
        var responses = new Queue<HttpResponseMessage>(
            Enumerable.Range(0, 2).Select(_ =>
            {
                var r = Response(HttpStatusCode.TooManyRequests, """{"error":{"code":"ApplicationThrottled","message":"x"}}""");
                r.Headers.Add("x-ms-ags-diagnostic", AgsDiagnostic);
                return r;
            }));
        var aggregate = await CaptureAsync(new RetryHandlerOption { MaxRetry = 1 }, responses);

        aggregate.Should().BeOfType<AggregateException>();
        GraphApiClient.TryReadApiFailure(aggregate!, out var failure).Should().BeTrue();

        failure.Status.Should().Be(429);
        failure.RetryAfter.Should().Be("0");
        failure.AgsDiagnostic.Should().Be(AgsDiagnostic);
    }

    [Fact]
    public async Task DeliveryRequest_ThroughGraphServiceClient_SendsTheQueueIdAsClientRequestId()
    {
        // The SDK's telemetry handler generates a random client-request-id unless one is set —
        // ours must survive the pipeline so Microsoft can find the request by our queue id.
        var stub = new StubHandler(new Queue<HttpResponseMessage>([new HttpResponseMessage(HttpStatusCode.Accepted)]));
        var client = GraphClient(stub);

        await client.Users["busy@example.com"].SendMail.PostAsync(
            new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody(),
            GraphApiClient.DeliveryRequest("0123456789ab4cdef0123456789abcde"));

        stub.LastRequest!.Headers.GetValues("client-request-id")
            .Should().ContainSingle().Which.Should().Be("01234567-89ab-4cde-f012-3456789abcde");
    }

    [Theory]
    [InlineData("0123456789ab4cdef0123456789abcde", "01234567-89ab-4cde-f012-3456789abcde")]
    [InlineData("01234567-89ab-4cde-f012-3456789abcde", "01234567-89ab-4cde-f012-3456789abcde")]
    [InlineData("msg-1", null)]                    // not a GUID — the SDK generates one as before
    public void ClientRequestIdFor_QueueId_IsSentInGuidForm(string messageId, string? expected)
        => GraphApiClient.ClientRequestIdFor(messageId).Should().Be(expected);

    [Fact]
    public void DescribeHeaders_NoHeaders_IsADash()
        => new GraphApiClient.ApiFailure(503, "x", "y", "n/a").DescribeHeaders().Should().Be("-");

    // =========================================================================
    // Helpers
    // =========================================================================

    private static Microsoft.Graph.GraphServiceClient GraphClient(StubHandler stub) => new(
        Microsoft.Graph.GraphClientFactory.Create(Microsoft.Graph.GraphClientFactory.CreateDefaultHandlers(), finalHandler: stub),
        new Microsoft.Kiota.Abstractions.Authentication.AnonymousAuthenticationProvider());

    // Retry-After: 0 keeps the SDK's back-off out of the test run.
    private static HttpResponseMessage Response(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("Retry-After", "0");
        return response;
    }

    private static async Task<Exception?> CaptureAsync(RetryHandlerOption option, Queue<HttpResponseMessage> responses)
    {
        using var http = new HttpClient(new RetryHandler(option) { InnerHandler = new StubHandler(responses) });
        try
        {
            await http.PostAsync("https://graph.test/users/x/sendMail", new StringContent("{}"));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class StubHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            var response = responses.Dequeue();
            // The retry handler clones the request from the response — a real HttpClient sets it too.
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
