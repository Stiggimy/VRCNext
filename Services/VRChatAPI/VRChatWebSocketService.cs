using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace VRCNext.Services;

public class FriendEventArgs : EventArgs
{
    public string  UserId   { get; init; } = "";
    public JObject? User    { get; init; }
    public string  Location { get; init; } = "";
    public string  WorldId  { get; init; } = "";
    public string  Platform { get; init; } = "";
}

public class NotificationEventArgs : EventArgs
{
    public string  WsType { get; init; } = "";
    public JObject? Data  { get; init; }
}

public sealed class VRChatWebSocketService : IDisposable
{
    // Public events

    public event EventHandler? FriendsChanged;

    public event EventHandler? FriendListChanged;

    public event EventHandler<NotificationEventArgs>? NotificationArrived;

    public event EventHandler<string>? OwnLocationChanged;

    public event EventHandler<JObject>? OwnUserUpdated;

    public event EventHandler? Connected;

    public event EventHandler? Disconnected;

    public event EventHandler<string>? ConnectError;

    public event EventHandler<FriendEventArgs>? FriendLocationChanged;

    public event EventHandler<FriendEventArgs>? FriendWentOffline;

    public event EventHandler<FriendEventArgs>? FriendWentOnline;

    public event EventHandler<FriendEventArgs>? FriendUpdated;

    public event EventHandler<FriendEventArgs>? FriendBecameActive;

    public event EventHandler<FriendEventArgs>? FriendAdded;

    public event EventHandler<FriendEventArgs>? FriendRemoved;

    // State

    private string _authToken = "";
    private string _tfaToken  = "";

    private Func<(string auth, string tfa)>? _getTokens;

    private CancellationTokenSource _cts = new();
    private Task? _connectTask;
    private bool _running;
    private bool _disposed;

    private System.Threading.Timer? _friendsDebounce;
    private const int FriendsDebounceMs = 500;

    private long _lastReceiveTicks = DateTime.UtcNow.Ticks;
    private const int HeartbeatTimeoutSec = 75;

    // Public API

    public void Start(string authToken, string tfaToken = "",
                      Func<(string auth, string tfa)>? getTokens = null)
    {
        var prevTask = _connectTask;
        Stop();
        _cts.Dispose();
        _authToken  = authToken;
        _tfaToken   = tfaToken;
        _getTokens  = getTokens;
        _running    = true;
        _cts        = new CancellationTokenSource();
        _connectTask = Task.Run(() => ConnectLoopAsync(_cts.Token));
        // Observe previous task so it doesn't become an unobserved faulted task
        prevTask?.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Stop()
    {
        _running = false;
        _cts.Cancel();
        _friendsDebounce?.Dispose();
        _friendsDebounce = null;
    }

    // Connection loop

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        int delaySec = 1;

        while (_running && !ct.IsCancellationRequested)
        {
            if (_getTokens != null)
            {
                try
                {
                    var (a, t) = _getTokens();
                    if (!string.IsNullOrEmpty(a))
                    {
                        _authToken = a;
                        _tfaToken  = t ?? "";
                    }
                }
                catch { }
            }

            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("User-Agent", "VRCNext/1.01.0 contact@vrcnext.app");

                var jar = new CookieContainer();
                var pipeUri = new Uri("https://pipeline.vrchat.cloud");
                jar.Add(pipeUri, new Cookie("auth", _authToken));
                if (!string.IsNullOrEmpty(_tfaToken))
                    jar.Add(pipeUri, new Cookie("twoFactorAuth", _tfaToken));
                ws.Options.Cookies = jar;

                var uri = new Uri($"wss://pipeline.vrchat.cloud/?authToken={Uri.EscapeDataString(_authToken)}");
                await ws.ConnectAsync(uri, ct);

                delaySec = 1;
                Interlocked.Exchange(ref _lastReceiveTicks, DateTime.UtcNow.Ticks);
                Connected?.Invoke(this, EventArgs.Empty);

                using var wdCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var receiveTask = ReceiveLoopAsync(ws, ct);
                var watchdogTask = WatchdogAsync(ws, wdCts.Token);

                await Task.WhenAny(receiveTask, watchdogTask);
                wdCts.Cancel();

                try { await receiveTask; } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ConnectError?.Invoke(this, ex.Message);
            }

            if (!_running || ct.IsCancellationRequested) break;

            Disconnected?.Invoke(this, EventArgs.Empty);

            try { await Task.Delay(TimeSpan.FromSeconds(delaySec), ct); }
            catch (OperationCanceledException) { break; }

            delaySec = Math.Min(delaySec * 2, 30);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            Interlocked.Exchange(ref _lastReceiveTicks, DateTime.UtcNow.Ticks);

            HandleMessage(sb.ToString());
        }
    }

    private async Task WatchdogAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try { await Task.Delay(30_000, ct); }
            catch (OperationCanceledException) { return; }

            var lastTicks = Interlocked.Read(ref _lastReceiveTicks);
            var elapsed   = DateTime.UtcNow - new DateTime(lastTicks);

            if (elapsed.TotalSeconds > HeartbeatTimeoutSec)
            {
                ConnectError?.Invoke(this, $"No data for {(int)elapsed.TotalSeconds}s — reconnecting");
                try { ws.Abort(); } catch { }
                return;
            }
        }
    }

    // Message routing

    private void HandleMessage(string json)
    {
        try
        {
            var msg = JObject.Parse(json);
            var type = msg["type"]?.Value<string>() ?? "";

            var contentStr = msg["content"]?.Value<string>() ?? "{}";

            switch (type)
            {
                case "friend-location":
                    DebounceFriendsRefresh();
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        var uid = c["userId"]?.Value<string>() ?? "";
                        var loc = c["location"]?.Value<string>() ?? "";
                        var wid = c["worldId"]?.Value<string>() ?? (loc.Contains(':') ? loc.Split(':')[0] : "");
                        var usr = c["user"] as JObject;
                        var plt = c["platform"]?.Value<string>() ?? usr?["platform"]?.Value<string>() ?? "";
                        FriendLocationChanged?.Invoke(this, new FriendEventArgs { UserId = uid, User = usr, Location = loc, WorldId = wid, Platform = plt });
                    }
                    catch { }
                    break;

                case "friend-offline":
                    DebounceFriendsRefresh();
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        var uid = c["userId"]?.Value<string>() ?? "";
                        var usr = c["user"] as JObject;
                        FriendWentOffline?.Invoke(this, new FriendEventArgs { UserId = uid, User = usr });
                    }
                    catch { }
                    break;

                case "friend-online":
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        var uid = c["userId"]?.Value<string>() ?? "";
                        var loc = c["location"]?.Value<string>() ?? "";
                        var plt = c["platform"]?.Value<string>() ?? "";
                        var usr = c["user"] as JObject;
                        FriendWentOnline?.Invoke(this, new FriendEventArgs { UserId = uid, User = usr, Location = loc, Platform = plt });
                    }
                    catch { }
                    break;

                case "friend-update":
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        var uid = c["userId"]?.Value<string>() ?? "";
                        var usr = c["user"] as JObject;
                        FriendUpdated?.Invoke(this, new FriendEventArgs { UserId = uid, User = usr });
                    }
                    catch { }
                    break;

                case "friend-active":
                    // friend-active = website/app activity, not a game login.
                    // Fires FriendBecameActive (NOT FriendWentOnline) so the friendslist
                    // updates without creating a "Came Online" timeline entry.
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        var uid = c["userid"]?.Value<string>() ?? c["userId"]?.Value<string>() ?? "";
                        var plt = c["platform"]?.Value<string>() ?? "";
                        var usr = c["user"] as JObject;
                        FriendBecameActive?.Invoke(this, new FriendEventArgs { UserId = uid, User = usr, Platform = plt });
                    }
                    catch { }
                    break;

                case "friend-add":
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        var uid = c["userId"]?.Value<string>() ?? "";
                        var usr = c["user"] as JObject;
                        FriendAdded?.Invoke(this, new FriendEventArgs { UserId = uid, User = usr });
                    }
                    catch { }
                    FriendListChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case "friend-delete":
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        var uid = c["userId"]?.Value<string>() ?? "";
                        FriendRemoved?.Invoke(this, new FriendEventArgs { UserId = uid });
                    }
                    catch { }
                    FriendListChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case "notification":
                case "notification-v2":
                case "notification-v2-update":
                case "notification-v2-delete":
                    try
                    {
                        var c = JObject.Parse(contentStr);
                        NotificationArrived?.Invoke(this, new NotificationEventArgs { WsType = type, Data = c });
                    }
                    catch
                    {
                        NotificationArrived?.Invoke(this, new NotificationEventArgs { WsType = type });
                    }
                    break;

                case "user-location":
                    try
                    {
                        var content = JObject.Parse(contentStr);
                        var location = content["location"]?.Value<string>() ?? "";
                        if (!string.IsNullOrEmpty(location))
                            OwnLocationChanged?.Invoke(this, location);
                    }
                    catch { }
                    break;

                case "user-update":
                    try
                    {
                        var content = JObject.Parse(contentStr);
                        var userObj = content["user"] as JObject;
                        if (userObj != null)
                            OwnUserUpdated?.Invoke(this, userObj);
                    }
                    catch { }
                    break;
            }
        }
        catch { }
    }

    private void DebounceFriendsRefresh()
    {
        if (_friendsDebounce == null)
            _friendsDebounce = new System.Threading.Timer(
                _ => FriendsChanged?.Invoke(this, EventArgs.Empty),
                null, FriendsDebounceMs, Timeout.Infinite);
        else
            _friendsDebounce.Change(FriendsDebounceMs, Timeout.Infinite);
    }

    // IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts.Dispose();
    }
}
