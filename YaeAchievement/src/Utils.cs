using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;
using Proto;
using YaeAchievement.res;
using YaeAchievement.Utilities;

namespace YaeAchievement;

public static class Utils {

    public static HttpClient CHttpClient { get; } = new (new HttpClientHandler {
        AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.GZip
    }) {
        DefaultRequestHeaders = {
            UserAgent = {
                new ProductInfoHeaderValue("YaeAchievement", GlobalVars.AppVersion.ToString(2))
            }
        }
    };

    public static async Task<byte[]> GetBucketFile(string path, bool useCache = true) {
        try {
            return await await Task.WhenAny(GetFile(GlobalVars.RinBucketHost), GetFile(GlobalVars.SakuraBucketHost));
        } catch (Exception e) when(e is SocketException or TaskCanceledException) {
            Console.WriteLine(App.NetworkError, e.Message);
            Environment.Exit(-1);
            return null!;
        }
        async Task<byte[]> GetFile(string host) {
            using var msg = new HttpRequestMessage();
            msg.Method = HttpMethod.Get;
            msg.RequestUri = new Uri($"{host}/{path}");
            CacheItem? cache = null;
            if (useCache && CacheFile.TryRead(path, out cache)) {
                msg.Headers.TryAddWithoutValidation("If-None-Match", $"{cache.Etag}");
            }
            using var response = await CHttpClient.SendAsync(msg);
            if (cache != null && response.StatusCode == HttpStatusCode.NotModified) {
                return cache.Content.ToByteArray();
            }
            response.EnsureSuccessStatusCode();
            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            if (useCache) {
                var etag = response.Headers.ETag!.Tag;
                CacheFile.Write(path, responseBytes, etag);
            }
            return responseBytes;
        }
    }

    public static T? GetOrNull<T>(this T[] array, uint index) where T : class {
        return array.Length > index ? array[index] : null;
    }

    public static uint? ToUIntOrNull(string? value) {
        return value != null ? uint.TryParse(value, out var result) ? result : null : null;
    }

    public static bool ToBooleanOrFalse(string? value) {
        return value != null && bool.TryParse(value, out var result) && result;
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

    public static async Task CheckUpdate(bool useLocalLib) {
        var info = UpdateInfo.Parser.ParseFrom(await GetBucketFile("schicksal/version"))!;
        if (GlobalVars.AppVersionCode < info.VersionCode) {
            Console.WriteLine(App.UpdateNewVersion, GlobalVars.AppVersionName, info.VersionName);
            Console.WriteLine(App.UpdateDescription, info.Description);
            if (info.EnableAutoUpdate) {
                Console.WriteLine(App.UpdateDownloading);
                var tmpPath = Path.GetTempFileName();
                await File.WriteAllBytesAsync(tmpPath, await GetBucketFile(info.PackageLink));
                var updaterPath = Path.Combine(GlobalVars.DataPath, "update.exe");
                await using (var dstStream = File.Open($"{GlobalVars.DataPath}/update.exe", FileMode.Create)) {
                    await using var srcStream = typeof(Program).Assembly.GetManifestResourceStream("updater")!;
                    await srcStream.CopyToAsync(dstStream);
                }
                ShellOpen(updaterPath, $"{Environment.ProcessId} \"{tmpPath}\"");
                await Task.Delay(1919810);
                GlobalVars.PauseOnExit = false;
                Environment.Exit(0);
            }
            Console.WriteLine(App.DownloadLink, info.PackageLink);
            if (info.ForceUpdate) {
                Environment.Exit(0);
            }
        }
        if (info.EnableLibDownload && !useLocalLib) {
            var data = await GetBucketFile("schicksal/lic.dll");
            await File.WriteAllBytesAsync(GlobalVars.LibFilePath, data);
        }
        _updateInfo = info;
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

    internal static void CheckGenshinIsRunning() {
        // QueryProcessEvent?
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var path in Directory.EnumerateDirectories($"{appdata}/../LocalLow/miHoYo").Where(p => File.Exists($"{p}/info.txt"))) {
            try {
                using var handle = File.OpenHandle($"{path}/output_log.txt", share: FileShare.None);
            } catch (IOException) {
                Console.WriteLine(App.GenshinIsRunning, 0);
                Environment.Exit(301);
            }
        }
    }

    private static GameProcess? _proc;

    public static void InstallExitHook() {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => {
            _proc?.Terminate(0);
            if (GlobalVars.PauseOnExit) {
                Console.WriteLine(App.PressKeyToExit);
                Console.ReadKey();
            }
        };
    }

    public static void InstallExceptionHook() {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => {
            var ex = e.ExceptionObject;
            switch (ex) {
                case ApplicationException ex1:
                    Console.WriteLine(ex1.Message);
                    break;
                case SocketException ex2:
                    Console.WriteLine(App.ExceptionNetwork, nameof(SocketException), ex2.Message);
                    break;
                case HttpRequestException ex3:
                    Console.WriteLine(App.ExceptionNetwork, nameof(HttpRequestException), ex3.Message);
                    break;
                default:
                    Console.WriteLine(ex.ToString());
                    break;
            }
            Environment.Exit(-1);
        };
    }

    private static bool _isUnexpectedExit = true;
    
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static void StartAndWaitResult(string exePath, Dictionary<int, Func<BinaryReader, bool>> handlers, Action onFinish) {
        _proc = new GameProcess(exePath);
        _proc.OnExit += () => {
            if (_isUnexpectedExit) {
                _proc = null;
                Console.WriteLine(App.GameProcessExit);
                Environment.Exit(114514);
            }
        };
        _proc.LoadLibrary(GlobalVars.LibFilePath);
        _proc.ResumeMainThread();
        Console.WriteLine(App.GameLoading, _proc.Id);
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
        });
    }

    public static unsafe void SetQuickEditMode(bool enable) {
        var handle = Native.GetStdHandle(STD_HANDLE.STD_INPUT_HANDLE);
        CONSOLE_MODE mode = default;
        Native.GetConsoleMode(handle, &mode);
        mode = enable ? mode | CONSOLE_MODE.ENABLE_QUICK_EDIT_MODE : mode &~CONSOLE_MODE.ENABLE_QUICK_EDIT_MODE;
        Native.SetConsoleMode(handle, mode);
    }
}
