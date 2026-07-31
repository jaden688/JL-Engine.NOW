using Microsoft.Playwright;

namespace JLEngine.Runtime.Tools;

/// <summary>Port of Tools.jl's tool_playwright_interact — extends browse_url
/// with real browser automation (click/fill/type/press/wait/select/read/
/// evaluate/screenshot) via a persistent headless Chromium context, matching
/// Julia's action-type DSL. One browser is lazily launched per session (this
/// tool instance) on first use and reused across calls, matching Julia's
/// `_state[:browser_context]` — each call still opens/closes its own page.
///
/// Requires the Playwright browser binary to be installed once via:
///   dotnet tool run playwright install chromium
/// (run from JLEngine.Runtime's or JLEngine.Host's output directory) before
/// first use — an unsandboxed local prerequisite, same class of requirement
/// as Julia's own already-initialized browser_context.</summary>
public sealed class PlaywrightInteractTool : ITool, IAsyncDisposable
{
    public string Name => "playwright_interact";

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private async Task<IBrowser> EnsureBrowserAsync()
    {
        if (_browser is not null) return _browser;
        await _initLock.WaitAsync();
        try
        {
            if (_browser is not null) return _browser;
            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            return _browser;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<Dictionary<string, object?>> DispatchAsync(Dictionary<string, object?> args)
    {
        var url = ToolArgs.GetArg(args, "url");
        var actions = args.TryGetValue("actions", out var a) && a is List<object?> list ? list : [];

        IBrowser browser;
        try
        {
            browser = await EnsureBrowserAsync();
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?>
            {
                ["error"] = $"Browser not initialized: {e.Message}. Run 'playwright install chromium' once, then retry.",
            };
        }

        var results = new List<object?>();
        IPage? page = null;
        try
        {
            page = await browser.NewPageAsync();
            if (!string.IsNullOrEmpty(url))
            {
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            }

            foreach (var actionObj in actions)
            {
                if (actionObj is not Dictionary<string, object?> action) continue;
                var atype = ToolArgs.GetArg(action, "type");
                var selector = ToolArgs.GetArg(action, "selector");
                var value = ToolArgs.GetArg(action, "value");
                var timeoutMs = action.TryGetValue("timeout_ms", out var t) && float.TryParse(t?.ToString(), out var tm) ? tm : 5000f;

                try
                {
                    switch (atype)
                    {
                        case "goto":
                            await page.GotoAsync(value, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                            results.Add(new Dictionary<string, object?> { ["type"] = "goto", ["url"] = value, ["ok"] = true });
                            break;
                        case "click":
                            await page.ClickAsync(selector, new PageClickOptions { Timeout = timeoutMs });
                            results.Add(new Dictionary<string, object?> { ["type"] = "click", ["selector"] = selector, ["ok"] = true });
                            break;
                        case "fill":
                            await page.FillAsync(selector, value, new PageFillOptions { Timeout = timeoutMs });
                            results.Add(new Dictionary<string, object?> { ["type"] = "fill", ["selector"] = selector, ["ok"] = true });
                            break;
                        case "type":
                            await page.Locator(selector).PressSequentiallyAsync(value, new LocatorPressSequentiallyOptions { Delay = 50 });
                            results.Add(new Dictionary<string, object?> { ["type"] = "type", ["selector"] = selector, ["ok"] = true });
                            break;
                        case "press":
                            await page.PressAsync(selector, value);
                            results.Add(new Dictionary<string, object?> { ["type"] = "press", ["key"] = value, ["ok"] = true });
                            break;
                        case "wait":
                            await page.WaitForTimeoutAsync(string.IsNullOrEmpty(value) ? 1000f : float.Parse(value));
                            results.Add(new Dictionary<string, object?> { ["type"] = "wait", ["ok"] = true });
                            break;
                        case "wait_for":
                            await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions { Timeout = timeoutMs });
                            results.Add(new Dictionary<string, object?> { ["type"] = "wait_for", ["selector"] = selector, ["ok"] = true });
                            break;
                        case "select":
                            await page.SelectOptionAsync(selector, value);
                            results.Add(new Dictionary<string, object?> { ["type"] = "select", ["selector"] = selector, ["value"] = value, ["ok"] = true });
                            break;
                        case "read":
                        {
                            var text = await page.EvaluateAsync<string>("() => document.body.innerText");
                            results.Add(new Dictionary<string, object?> { ["type"] = "read", ["content"] = text.Length > 4000 ? text[..4000] : text, ["ok"] = true });
                            break;
                        }
                        case "evaluate":
                        {
                            var resultJs = await page.EvaluateAsync<string>(value);
                            results.Add(new Dictionary<string, object?> { ["type"] = "evaluate", ["result"] = resultJs?.Length > 2000 ? resultJs[..2000] : resultJs, ["ok"] = true });
                            break;
                        }
                        case "screenshot":
                        {
                            var path = ToolArgs.GetArg(action, "path");
                            if (string.IsNullOrEmpty(path)) path = Path.Combine(Path.GetTempPath(), "sparkbyte_screenshot.png");
                            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
                            results.Add(new Dictionary<string, object?> { ["type"] = "screenshot", ["path"] = path, ["ok"] = true });
                            break;
                        }
                        default:
                            results.Add(new Dictionary<string, object?> { ["type"] = atype, ["ok"] = false, ["error"] = $"Unknown action type: {atype}" });
                            break;
                    }
                }
                catch (Exception e)
                {
                    var msg = e.Message.Length > 300 ? e.Message[..300] : e.Message;
                    results.Add(new Dictionary<string, object?> { ["type"] = atype, ["ok"] = false, ["error"] = msg });
                }
            }

            return new Dictionary<string, object?> { ["results"] = results, ["url"] = url, ["action_count"] = results.Count };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object?> { ["error"] = e.Message };
        }
        finally
        {
            if (page is not null) await page.CloseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}
