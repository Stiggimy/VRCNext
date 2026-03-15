using Velopack;
using Velopack.Sources;

namespace VRCNext.Services;

public class UpdateService
{
    private const string RepoUrl = "https://github.com/shinyflvre/VRCNext";

    private UpdateManager? _mgr;
    private UpdateInfo?    _pending;

    public UpdateService()
    {
        try { _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false)); }
        catch { }
    }

    public async Task<string?> CheckAsync()
    {
        if (_mgr == null) return null;
        try
        {
            _pending = await _mgr.CheckForUpdatesAsync();
            return _pending?.TargetFullRelease.Version.ToString();
        }
        catch { return null; }
    }

    public async Task DownloadAsync(Action<int> onProgress)
    {
        if (_mgr == null || _pending == null) return;
        await _mgr.DownloadUpdatesAsync(_pending, onProgress);
    }

    public void ApplyAndRestart()
    {
        if (_mgr == null || _pending == null) return;
        _mgr.ApplyUpdatesAndRestart(_pending);
    }
}
