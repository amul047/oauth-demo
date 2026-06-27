using System.Net;
using System.Web;

namespace SimpleAuth.Client;

public sealed class CallbackServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly TaskCompletionSource<CallbackResult> _callbackSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _loopCancellation;
    private Task? _listenTask;

    public CallbackServer(string prefix = "http://localhost:3030/")
    {
        _listener.Prefixes.Add(prefix);
    }

    public Task StartAsync()
    {
        _listener.Start();
        _loopCancellation = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoopAsync(_loopCancellation.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_loopCancellation is not null && !_loopCancellation.IsCancellationRequested)
        {
            _loopCancellation.Cancel();
        }

        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask;
            }
            catch (Exception)
            {
            }
        }
    }

    public async Task<CallbackResult> WaitForCallbackAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        return await _callbackSource.Task.WaitAsync(timeoutSource.Token);
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !_listener.IsListening)
            {
                break;
            }

            var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
            var code = query["code"];
            var state = query["state"];
            var error = query["error"];

            if (!string.IsNullOrWhiteSpace(code))
            {
                _callbackSource.TrySetResult(new CallbackResult(code, state, null));
                await WriteHtmlAsync(context.Response, SuccessPage, HttpStatusCode.OK);
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                _callbackSource.TrySetResult(new CallbackResult(null, state, error));
                await WriteHtmlAsync(context.Response, FailurePage(error), HttpStatusCode.BadRequest);
            }
            else
            {
                await WriteHtmlAsync(context.Response, "<h1>Missing OAuth response.</h1>", HttpStatusCode.BadRequest);
            }
        }
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse response, string html, HttpStatusCode statusCode)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "text/html; charset=utf-8";
        await using var writer = new StreamWriter(response.OutputStream);
        await writer.WriteAsync(html);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private const string SuccessPage = """
<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8" /><title>Authorization complete</title></head>
<body style="font-family: Arial, sans-serif; max-width: 640px; margin: 48px auto;">
    <h1>Authorization successful</h1>
    <p>You can close this window and return to the terminal.</p>
</body>
</html>
""";

    private static string FailurePage(string error) => $"""
<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8" /><title>Authorization failed</title></head>
<body style="font-family: Arial, sans-serif; max-width: 640px; margin: 48px auto;">
    <h1>Authorization failed</h1>
    <p>Error: {WebUtility.HtmlEncode(error)}</p>
</body>
</html>
""";
}

public sealed record CallbackResult(string? Code, string? State, string? Error);
