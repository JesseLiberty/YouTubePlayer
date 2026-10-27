using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using YouTubeWeb.Components.Pages;
using YouTubeWeb.Components.Shared;
using YouTubeWeb.Models;
using YouTubeWeb.Services;

#pragma warning disable BL0006 // Exercise component event handlers with an in-process test renderer.
internal static class WebDialogVerification
{
    public static async Task RunAsync()
    {
        await VerifyAsync("HelpButton_Click", MessageBoxResult.OK);
        await VerifyAsync("Unsubscribe_Click", MessageBoxResult.No);
        await VerifyAsync("Unsubscribe_Click", MessageBoxResult.Yes);
        await VerifyAsync("Unsubscribe_Click", MessageBoxResult.Yes, fail: true);
        Console.WriteLine("PASS: web search resumes after Help and declined/successful/failed Unsubscribe.");
    }

    private static async Task VerifyAsync(string method, MessageBoxResult answer, bool fail = false)
    {
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IJSRuntime, NoOpJs>()
            .AddSingleton<YouTubeClient>().BuildServiceProvider();
        using var service = new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientFactory = new FakeHttpFactory(fail)
        });
        Set(services.GetRequiredService<YouTubeClient>(), "_service", service);
        using var renderer = new TestRenderer(services);
        var home = new Home();
        typeof(Home).GetProperty("Client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(home, services.GetRequiredService<YouTubeClient>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderAsync(home);
            var column = new SeriesColumn { SubscriptionId = "test", ChannelId = "test", Title = "Test" };
            var columns = (ObservableCollection<SeriesColumn>)Get(home, "_series")!;
            columns.Add(column);
            Set(home, "_searchText", "semantic query");
            using var interrupted = new CancellationTokenSource();
            var interruptedToken = interrupted.Token;
            Set(home, "_semanticSearchCancellation", interrupted);
            var operation = (Task)Invoke(home, method, method == "HelpButton_Click" ? [] : [column])!;
            Require(interruptedToken.IsCancellationRequested, "Foreground dialog did not cancel search.");
            Require(Get(home, "_semanticSearchCancellation") is null, "Search restarted before dialog closed.");
            var box = (MessageBox)Get(home, "_messageBox")!;
            Invoke(box, "Complete", [answer]);
            if (fail)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (Get(box, "_request") is null && !operation.IsCompleted && DateTime.UtcNow < deadline)
                    await Task.Delay(10);
                Require(Get(box, "_request") is not null, "Unsubscribe failure dialog did not appear.");
                Require(Get(home, "_semanticSearchCancellation") is null, "Search restarted before error acknowledgement.");
                Invoke(box, "Complete", [MessageBoxResult.OK]);
            }
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            Require(Get(home, "_semanticSearchCancellation") is CancellationTokenSource,
                "Current query was not resumed after the dialog workflow.");
            Require((string)Get(home, "_searchText")! == "semantic query", "Resuming changed the query.");
            var removed = method == "Unsubscribe_Click" && answer == MessageBoxResult.Yes && !fail;
            Require(columns.Contains(column) != removed, "Unexpected subscription changes.");
            home.Dispose();
        });
    }

    private static object? Get(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestRenderer : Renderer
    {
        public TestRenderer(IServiceProvider services)
            : base(services, services.GetRequiredService<ILoggerFactory>())
        {
            ElementReferenceContext = new WebElementReferenceContext(services.GetRequiredService<IJSRuntime>());
        }

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public Task RenderAsync(IComponent component) =>
            RenderRootComponentAsync(AssignRootComponentId(component), ParameterView.Empty);
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Render failed.", exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    private sealed class NoOpJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class FakeHttpFactory(bool fail) : Google.Apis.Http.IHttpClientFactory
    {
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) =>
            new(new ConfigurableMessageHandler(new FakeHandler(fail)));
    }

    private sealed class FakeHandler(bool fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(fail ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent)
            {
                Content = new StringContent(fail ? """{"error":{"code":400,"message":"Test failure"}}""" : "")
            });
    }
}
