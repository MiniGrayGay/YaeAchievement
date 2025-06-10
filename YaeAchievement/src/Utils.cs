using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;
using Proto;
using Spectre.Console;
using YaeAchievement.Utilities;

namespace YaeAchievement;

public static class Utils {

    public static HttpClient CHttpClient { get; } = new (new SentryHttpMessageHandler(new HttpClientHandler {
        AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.GZip
    })) {
        DefaultRequestHeaders = {
            UserAgent = {
                new ProductInfoHeaderValue("YaeAchievement", GlobalVars.AppVersion.ToString(2))
            }
        }
    };

    public static async Task<byte[]> GetBucketFile(string path, bool useCache = true) {
        var transaction = SentrySdk.StartTransaction(path, "bucket.get");
        SentrySdk.ConfigureScope(scope => scope.Transaction = transaction);
        try {
            var data = await GetFile("https://api.qhy04.com/hutaocdn/download?filename={0}", path, useCache);
            transaction.Finish();
            return data;
        } catch (Exception e) when (e is SocketException or TaskCanceledException or HttpRequestException) {
        }
        try {
            var data = await Task.WhenAny(
                GetFile("https://rin.holohat.work/{0}", path, useCache),
                GetFile("https://cn-cd-1259389942.file.myqcloud.com/{0}", path, useCache)
            ).Unwrap();
            transaction.Finish();
            return data;
        } catch (Exception ex) when (ex is SocketException or TaskCanceledException) {
            transaction.Finish();
            AnsiConsole.WriteLine(App.NetworkError, ex.Message);
            Environment.Exit(-1);
        }
        throw new UnreachableException();
        static async Task<byte[]> GetFile(string baseUrl, string objectKey, bool useCache) {
            using var reqwest = new HttpRequestMessage(HttpMethod.Get, string.Format(baseUrl, objectKey));
            CacheItem? cache = null;
            if (useCache && CacheFile.TryRead(objectKey, out cache)) {
                reqwest.Headers.TryAddWithoutValidation("If-None-Match", $"{cache.Etag}");
            }
            using var response = await CHttpClient.SendAsync(reqwest);
            if (cache != null && response.StatusCode == HttpStatusCode.NotModified) {
                return cache.Content.ToByteArray();
            }
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (useCache) {
                var etag = response.Headers.ETag!.Tag;
                CacheFile.Write(objectKey, bytes, etag);
            }
            return bytes;
        }
    }

    public static T? GetOrNull<T>(this T[] array, uint index) where T : class {
        return array.Length > index ? array[index] : null;
    }

    public static int ToIntOrDefault(string? value, int defaultValue = 0) {
        return value != null && int.TryParse(value, out var result) ? result : defaultValue;
    }

    public static bool ToBooleanOrDefault(string? value, bool defaultValue = false) {
        return value != null && bool.TryParse(value, out var result) ? result : defaultValue;
    }

    public static unsafe void CopyToClipboard(string text) {
        if (Native.OpenClipboard(HWND.Null)) {
            Native.EmptyClipboard();
            var hGlobal = (HGLOBAL) Marshal.AllocHGlobal((text.Length + 1) * 2);
            var hPtr = (nint) Native.GlobalLock(hGlobal);
            Marshal.Copy(text.ToCharArray(), 0, hPtr, text.Length);
            Native.GlobalUnlock((HGLOBAL) hPtr);
            Native.SetClipboardData(13,  new HANDLE(hPtr));
            Marshal.FreeHGlobal(hGlobal);
            Native.CloseClipboard();
        } else {
            throw new Win32Exception();
        }
    }

    // ReSharper disable once NotAccessedField.Local
    private static UpdateInfo _updateInfo = null!;

    public static Task StartSpinnerAsync(string status, Func<StatusContext, Task> func) {
        return AnsiConsole.Status().Spinner(Spinner.Known.SimpleDotsScrolling).StartAsync(status, func);
    }

    public static Task<T> StartSpinnerAsync<T>(string status, Func<StatusContext, Task<T>> func) {
        return AnsiConsole.Status().Spinner(Spinner.Known.SimpleDotsScrolling).StartAsync(status, func);
    }

    public static async Task CheckUpdate(bool useLocalLib) {
        try {
            var versionData = await StartSpinnerAsync(App.UpdateChecking, _ => GetBucketFile("schicksal/version"));
            var versionInfo = UpdateInfo.Parser.ParseFrom(versionData)!;
            if (GlobalVars.AppVersionCode < versionInfo.VersionCode) {
                AnsiConsole.WriteLine(App.UpdateNewVersion, GlobalVars.AppVersionName, versionInfo.VersionName);
                AnsiConsole.WriteLine(App.UpdateDescription, versionInfo.Description);
                if (versionInfo.EnableAutoUpdate) {
                    var newBin = await StartSpinnerAsync(App.UpdateDownloading, _ => GetBucketFile(versionInfo.PackageLink));
                    var tmpPath = Path.GetTempFileName();
                    var updaterPath = Path.Combine(GlobalVars.DataPath, "update.exe");
                    await using (var dstStream = File.Open($"{GlobalVars.DataPath}/update.exe", FileMode.Create)) {
                        await using var srcStream = typeof(Program).Assembly.GetManifestResourceStream("updater")!;
                        await srcStream.CopyToAsync(dstStream);
                    }
                    await File.WriteAllBytesAsync(tmpPath, newBin);
                    ShellOpen(updaterPath, $"{Environment.ProcessId} \"{tmpPath}\"");
                    await StartSpinnerAsync(App.UpdateChecking, _ => Task.Delay(1919810));
                    GlobalVars.PauseOnExit = false;
                    Environment.Exit(0);
                }
                AnsiConsole.MarkupLine($"[link]{App.DownloadLink}[/]", versionInfo.PackageLink);
                if (versionInfo.ForceUpdate) {
                    Environment.Exit(0);
                }
            }
            if (versionInfo.EnableLibDownload && !useLocalLib) {
                var data = await GetBucketFile("schicksal/lic.dll");
                await File.WriteAllBytesAsync(GlobalVars.LibFilePath, data); // 要求重启电脑
            }
            _updateInfo = versionInfo;
        } catch (IOException e) when ((uint) e.HResult == 0x80070020) { // ERROR_SHARING_VIOLATION
            // IO_SharingViolation_File
            // The process cannot access the file '{0}' because it is being used by another process.
            AnsiConsole.WriteLine("文件 {0} 被其它程序占用，请 重启电脑 或 解除文件占用 后重试。", e.Message[36..^46]);
            Environment.Exit(-1);
        }
    }

    // ReSharper disable once UnusedMethodReturnValue.Global
    public static bool ShellOpen(string path, string? args = null) {
        try {
            var startInfo = new ProcessStartInfo {
                FileName = path,
                UseShellExecute = true
            };
            if (args != null) {
                startInfo.Arguments = args;
            }
            return new Process {
                StartInfo = startInfo
            }.Start();
        } catch (Exception) {
            return false;
        }
    }

    internal static Process? GetGameProcess() => Process.GetProcessesByName("YuanShen")
        .Concat(Process.GetProcessesByName("GenshinImpact"))
        .FirstOrDefault(p => File.Exists($"{p.GetFileName()}/../HoYoKProtect.sys"));

    private static GameProcess? _proc;

    public static void InstallExitHook() {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => {
            _proc?.Terminate(0);
            if (GlobalVars.PauseOnExit) {
                AnsiConsole.WriteLine(App.PressKeyToExit);
                Console.ReadKey();
            }
        };
    }

    public static void InstallExceptionHook() {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnUnhandledException((Exception) e.ExceptionObject);
    }

    public static void OnUnhandledException(Exception ex) {
        SentrySdk.CaptureException(ex);
        switch (ex) {
            case ApplicationException ex1:
                AnsiConsole.WriteLine(ex1.Message);
                break;
            case SocketException ex2:
                AnsiConsole.WriteLine(App.ExceptionNetwork, nameof(SocketException), ex2.Message);
                break;
            case HttpRequestException ex3:
                AnsiConsole.WriteLine(App.ExceptionNetwork, nameof(HttpRequestException), ex3.Message);
                break;
            default:
                AnsiConsole.WriteLine(ex.ToString());
                break;
        }
        Environment.Exit(-1);
    }

    private static bool _isUnexpectedExit = true;
    
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static void StartAndWaitResult(string exePath, Dictionary<int, Func<BinaryReader, bool>> handlers, Action onFinish) {
        _proc = new GameProcess(exePath);
        _proc.OnExit += () => {
            if (_isUnexpectedExit) {
                _proc = null;
                AnsiConsole.WriteLine(App.GameProcessExit);
                Environment.Exit(114514);
            }
        };
        _proc.LoadLibrary(GlobalVars.LibFilePath);
        _proc.ResumeMainThread();
        AnsiConsole.WriteLine(App.GameLoading, _proc.Id);
        Task.Run(() => {
            using var stream = new NamedPipeServerStream(GlobalVars.PipeName);
            using var reader = new BinaryReader(stream);
            stream.WaitForConnection();
            int type;
            while ((type = stream.ReadByte()) != -1) {
                if (type == 0xFF) {
                    _isUnexpectedExit = false;
                    onFinish();
                    break;
                }
                if (handlers.TryGetValue(type, out var handler)) {
                    if (handler(reader)) {
                        handlers.Remove(type);
                    }
                }
            }
        }).ContinueWith(task => { if (task.IsFaulted) OnUnhandledException(task.Exception!); });
    }

    internal static unsafe void SetQuickEditMode(bool enable) {
        var handle = Native.GetStdHandle(STD_HANDLE.STD_INPUT_HANDLE);
        CONSOLE_MODE mode = default;
        Native.GetConsoleMode(handle, &mode);
        mode = enable ? mode | CONSOLE_MODE.ENABLE_QUICK_EDIT_MODE : mode &~CONSOLE_MODE.ENABLE_QUICK_EDIT_MODE;
        Native.SetConsoleMode(handle, mode);
    }

    internal static unsafe void FixTerminalFont() {
        if (!CultureInfo.CurrentCulture.Name.StartsWith("zh")) {
            return;
        }
        var handle = Native.GetStdHandle(STD_HANDLE.STD_OUTPUT_HANDLE);
        var fontInfo = new CONSOLE_FONT_INFOEX {
            cbSize = (uint) sizeof(CONSOLE_FONT_INFOEX)
        };
        if (!Native.GetCurrentConsoleFontEx(handle, false, &fontInfo)) {
            return;
        }
        if (fontInfo.FaceName.ToString() == "Terminal") { // 点阵字体
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US"); // todo: use better way like auto set console font etc.
        }
    }
}
