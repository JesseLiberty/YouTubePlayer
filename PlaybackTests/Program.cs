using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using YouTubeWeb.Components.Shared;
using YouTubeWeb.Models;

#pragma warning disable BL0006 // Exercise component lifecycle without a browser or external services.
await VerifyRetryAsync();
await VerifyPendingCloseAsync();
await VerifyPendingImportDisposalAsync();
await VerifyCleanupFailureAsync();
Console.WriteLine("PASS: playback retry, stale callbacks, pending-open close, disposal, and cleanup failures.");

static async Task VerifyRetryAsync()
{
    await using var fixture = new Fixture();
    fixture.Js.FailImport = true;
    await fixture.RunAsync(async () =>
    {
        await fixture.RenderAsync();
        var completion = fixture.Component.PlayAsync("video", "Video", 12);
        Require(fixture.Text.Contains("could not start"), "Initialization error was not rendered.");
        Require(!completion.IsCompleted, "Initialization failure prematurely completed playback.");
        fixture.Js.FailImport = false;
        await InvokeAsync(fixture.Component, "Retry");
        var id = fixture.Js.Module.SessionId!;
        fixture.Component.OnPosition(id, 55);
        fixture.Component.OnStarted(id);
        fixture.Component.OnWatched("stale");
        await fixture.Component.OnEnded("stale", 100);
        Require(!completion.IsCompleted, "A stale session ended current playback.");
        await fixture.Component.OnEnded(id, 100);
        var result = await completion;
        Require(result.PositionSeconds == 100 && result.WatchLevel == WatchLevel.Started,
            "Retry lost the original completion or accepted stale watch progress.");
        Require(fixture.Js.Module.Player.Released == 1, "Player reference was not released on end.");
    });
}

static async Task VerifyPendingCloseAsync()
{
    await using var fixture = new Fixture();
    fixture.Js.Module.PendingOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await fixture.RunAsync(async () =>
    {
        await fixture.RenderAsync();
        var completion = fixture.Component.PlayAsync("video", "Video", 0);
        Require(fixture.Js.Module.Opens == 1, "Open did not start.");
        await InvokeAsync(fixture.Component, "Close");
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.Js.Module.Closes == 1, "Pending JS open was not cancelled.");
        fixture.Js.Module.PendingOpen.SetResult(fixture.Js.Module.Player);
        await fixture.Js.Module.Player.Release.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.Js.Module.Player.Disposes == 1, "Late open was not disposed.");
    });
}

static async Task VerifyPendingImportDisposalAsync()
{
    await using var fixture = new Fixture();
    fixture.Js.PendingImport = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await fixture.RunAsync(async () =>
    {
        await fixture.RenderAsync();
        var completion = fixture.Component.PlayAsync("video", "Video", 0);
        var disposal = fixture.Component.DisposeAsync().AsTask();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Js.PendingImport.SetResult(fixture.Js.Module);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.Js.Module.Opens == 0, "A disposed component opened a player.");
        Require(fixture.Js.Module.Released == 1, "A late import reference was leaked.");
    });
}

static async Task VerifyCleanupFailureAsync()
{
    await using var fixture = new Fixture();
    fixture.Js.Module.Player.FailDispose = true;
    await fixture.RunAsync(async () =>
    {
        await fixture.RenderAsync();
        var completion = fixture.Component.PlayAsync("video", "Video", 0);
        await InvokeAsync(fixture.Component, "Close");
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.Js.Module.Player.Released == 1, "JS cleanup failure leaked the .NET reference.");
        var next = fixture.Component.PlayAsync("next", "Next", 0);
        var id = fixture.Js.Module.SessionId!;
        fixture.Component.OnPosition(id, 30);
        await fixture.Component.DisposeAsync();
        var result = await next;
        Require(result.PositionSeconds == 30, "Disposal lost playback position.");
        await fixture.Component.DisposeAsync();
        Require(fixture.Js.Module.Released == 1, "Repeated disposal released the module twice.");
    });
}

static Task InvokeAsync(PlayerWindow component, string name) =>
    (Task)typeof(PlayerWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, null)!;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Fixture : Renderer
{
    public FakeJs Js { get; }
    public PlayerWindow Component { get; } = new();
    private int _root;

    public Fixture() : this(new ServiceCollection().AddLogging().BuildServiceProvider(), new FakeJs()) { }
    private Fixture(IServiceProvider services, FakeJs js)
        : base(services, services.GetRequiredService<ILoggerFactory>())
    {
        Js = js;
        ElementReferenceContext = new WebElementReferenceContext(js);
        typeof(PlayerWindow).GetProperty("JS", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Component, js);
    }

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
    public Task RunAsync(Func<Task> action) => Dispatcher.InvokeAsync(action);
    public Task RenderAsync()
    {
        _root = AssignRootComponentId(Component);
        return RenderRootComponentAsync(_root, ParameterView.Empty);
    }
    public string Text => string.Join(" ", GetCurrentRenderTreeFrames(_root).Array
        .Take(GetCurrentRenderTreeFrames(_root).Count)
        .Where(frame => frame.FrameType == RenderTreeFrameType.Text).Select(frame => frame.TextContent));
    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    protected override void HandleException(Exception exception) => throw new InvalidOperationException("Render failed.", exception);
}

sealed class FakeJs : IJSRuntime
{
    public FakeModule Module { get; } = new();
    public bool FailImport { get; set; }
    public TaskCompletionSource<IJSObjectReference>? PendingImport { get; set; }
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
        InvokeAsync<T>(identifier, CancellationToken.None, args);
    public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        if (identifier != "import") return default!;
        if (FailImport) throw new JSException("Failed to load module.");
        return (T)(object)(PendingImport is { } pending ? await pending.Task : Module);
    }
}

sealed class FakeModule : IJSObjectReference
{
    public FakePlayer Player { get; } = new();
    public int Opens { get; private set; }
    public int Closes { get; private set; }
    public int Released { get; private set; }
    public string? SessionId { get; private set; }
    public TaskCompletionSource<IJSObjectReference>? PendingOpen { get; set; }
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
        InvokeAsync<T>(identifier, CancellationToken.None, args);
    public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        if (identifier == "close") { Closes++; return default!; }
        if (identifier != "open") throw new JSException(identifier);
        Opens++;
        SessionId = (string)args![4]!;
        return (T)(object)(PendingOpen is { } pending ? await pending.Task : Player);
    }
    public ValueTask DisposeAsync() { Released++; return ValueTask.CompletedTask; }
}

sealed class FakePlayer : IJSObjectReference
{
    public int Disposes { get; private set; }
    public int Released { get; private set; }
    public bool FailDispose { get; set; }
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
        InvokeAsync<T>(identifier, CancellationToken.None, args);
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Disposes++;
        if (FailDispose) throw new JSException("Player cleanup failed.");
        return ValueTask.FromResult(default(T)!);
    }
    public ValueTask DisposeAsync()
    {
        Released++;
        Release.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
