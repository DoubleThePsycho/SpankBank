using Microsoft.AspNetCore.Mvc.Filters;

namespace SpankBank.Web.Services;

/// <summary>After any successful change on the watches pages, tells the worker so it can re-match without delay.</summary>
public sealed class WatchChangeNotifier : IAsyncPageFilter
{
    private readonly WorkerClient _worker;

    public WatchChangeNotifier(WorkerClient worker) => _worker = worker;

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var executed = await next();

        var request = context.HttpContext.Request;
        if (HttpMethods.IsPost(request.Method)
            && request.Path.StartsWithSegments("/watches")
            && executed.Exception is null
            && context.HttpContext.Response.StatusCode < 400
            && executed.Result is not Microsoft.AspNetCore.Mvc.ContentResult { StatusCode: >= 400 })
        {
            _ = _worker.NotifyAsync("match");
        }
    }
}
