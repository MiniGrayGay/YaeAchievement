﻿using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Proto;
using YaeAchievement.res;
using YaeAchievement.Utilities;

namespace YaeAchievement;

public static class Utils {

    public static readonly HttpClient CHttpClient = new (new HttpClientHandler {
        AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.GZip
    }) {
        DefaultRequestHeaders = {
            UserAgent = {
                new ProductInfoHeaderValue("YaeAchievement", GlobalVars.AppVersion.ToString(2))
            }
        }
    };

    public static async Task<byte[]> GetBucketFile(string path, bool cache = true) {
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
            var cacheFile = new CacheFile(path);
            if (cache && cacheFile.Exists()) {
                msg.Headers.TryAddWithoutValidation("If-None-Match", $"{cacheFile.Read().Etag}");
            }
            using var response = await CHttpClient.SendAsync(msg);
            if (cache && response.StatusCode == HttpStatusCode.NotModified) {
                return cacheFile.Read().Content.ToByteArray();
            }
            response.EnsureSuccessStatusCode();
            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            if (cache) {
                var etag = response.Headers.ETag!.Tag;
                cacheFile.Write(responseBytes, etag);
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
                var updaterArgs = $"{Environment.ProcessId}|{Environment.ProcessPath}|{tmpPath}";
                var updaterPath = Path.Combine(GlobalVars.DataPath, "update.exe");
                await File.WriteAllBytesAsync(updaterPath, App.Updater);
                ShellOpen(updaterPath, updaterArgs.ToBytes().ToBase64());
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

    public static void CheckGenshinIsRunning() {
        Process.EnterDebugMode();
        foreach (var process in Process.GetProcesses()) {
            if (process.ProcessName is "GenshinImpact" or "YuanShen"
                && !process.HasExited
                && process.MainWindowHandle != nint.Zero
            ) {
                Console.WriteLine(App.GenshinIsRunning, process.Id);
                Environment.Exit(301);
            }
        }
        Process.LeaveDebugMode();
    }

    // ReSharper disable once InconsistentNaming
    private static Process? proc;

    public static void InstallExitHook() {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => {
            proc?.Kill();
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

    private static bool _isUnexpectedExit;
    
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static Thread StartAndWaitResult(string exePath, Dictionary<byte, Func<BinaryReader, bool>> handlers, Action onFinish) {
        if (!Injector.CreateProcess(exePath, out var hProcess, out var hThread, out var pid)) {
            Environment.Exit(new Win32Exception().PrintMsgAndReturnErrCode("ICreateProcess fail"));
        }
        if (Injector.LoadLibraryAndInject(hProcess,GlobalVars.LibFilePath.AsSpan()) != 0)
        {
            if (!Native.TerminateProcess(hProcess, 0))
            {
                Environment.Exit(new Win32Exception().PrintMsgAndReturnErrCode("TerminateProcess fail"));
            }
        }
        Console.WriteLine(App.GameLoading, pid);
        proc = Process.GetProcessById(Convert.ToInt32(pid));
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => {
            if (_isUnexpectedExit)
            {
                proc = null;
                Console.WriteLine(App.GameProcessExit);
                Environment.Exit(114514);
            }
        };
        if (Native.ResumeThread(hThread) == 0xFFFFFFFF)
        {
            var e = new Win32Exception();
            if (!Native.TerminateProcess(hProcess, 0))
            {
                new Win32Exception().PrintMsgAndReturnErrCode("TerminateProcess fail");
            }
            Environment.Exit(e.PrintMsgAndReturnErrCode("ResumeThread fail"));
        }
        if (!Native.CloseHandle(hProcess))
        {
            Environment.Exit(new Win32Exception().PrintMsgAndReturnErrCode("CloseHandle fail"));
        }

        var ts = new ThreadStart(() => {
            var server = new NamedPipeServerStream(GlobalVars.PipeName);
            server.WaitForConnection();
            using var reader = new BinaryReader(server);
            while (!proc.HasExited) {
                var type = reader.ReadByte();
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
        var th = new Thread(ts);
        th.Start();
        return th;
    }
}
