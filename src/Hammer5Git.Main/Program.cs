using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Hammer5Git.Main;

public static class Program
{
    private const string MutexName = @"Global\Hammer5Git_SingleInstance_Mutex";
    private static Mutex? _singleInstanceMutex;

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_ICONERROR = 0x00000010;

    [STAThread]
    public static void Main(string[] args)
    {
        // 1. 全局未捕获异常早期守护
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "crash_error.log");
                var message = $"[AppDomain Unhandled] {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{e.ExceptionObject}";
                File.AppendAllText(logPath, message + "\r\n\r\n");
            }
            catch { }
        };

        // 2. 单实例互斥锁检测（防止多开引发 Git 仓库与文件监听冲突）
        bool isFirstInstance = false;
        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out isFirstInstance);
        }
        catch (AbandonedMutexException)
        {
            isFirstInstance = true;
        }
        catch
        {
            isFirstInstance = true;
        }

        if (!isFirstInstance)
        {
            MessageBox(
                IntPtr.Zero,
                "Hammer5Git 已在后台或桌面运行中，请勿重复多开。\n如未看到窗口，请检查任务栏右下角系统托盘图标。",
                "Hammer5Git 提示",
                MB_OK | MB_ICONWARNING);
            return;
        }

        try
        {
            // 3. WinUI 3 环境预检与 COM Wrappers 初始化
            XamlCheckProcessRequirements();
            WinRT.ComWrappersSupport.InitializeComWrappers();

            // 4. 启动 WinUI 3 Application 循环
            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        catch (Exception ex)
        {
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "crash_error.log");
                File.AppendAllText(logPath, $"[Main Exception] {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{ex}\r\n\r\n");
            }
            catch { }

            MessageBox(
                IntPtr.Zero,
                $"Hammer5Git 启动发生严重异常：\n{ex.Message}\n\n详细崩溃日志已记录至 crash_error.log",
                "Hammer5Git 致命错误",
                MB_OK | MB_ICONERROR);
        }
        finally
        {
            if (_singleInstanceMutex != null)
            {
                try
                {
                    if (isFirstInstance)
                    {
                        _singleInstanceMutex.ReleaseMutex();
                    }
                }
                catch { }
                _singleInstanceMutex.Dispose();
            }
        }
    }
}
