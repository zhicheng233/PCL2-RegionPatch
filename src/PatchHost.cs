using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace PclRuntime
{
    /// <summary>
    /// 等待 PCL 入口程序集与内嵌依赖就绪，然后应用全部 Harmony 补丁。
    /// </summary>
    internal static class PatchHost
    {
        public static void Run()
        {
            try
            {
                Log.Reset();
                Log.Info("PatchHost started");

                if (!WaitForPcl()) { Log.Info("PatchHost: PCL 未在 30s 内就绪，放弃"); return; }

                if (Environment.GetEnvironmentVariable("PCLRT_NOPATCH") == "1")
                {
                    Log.Info("PatchHost: PCLRT_NOPATCH=1，跳过打补丁");
                    return;
                }

                var harmony = new Harmony("pcl.inject.runtime");
                Patches.ApplyAll(harmony);

                Log.Info("PatchHost: done");
            }
            catch (Exception ex)
            {
                Log.Info("PatchHost crashed: " + ex);
            }
        }

        private static bool WaitForPcl()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (Assembly.GetEntryAssembly() != null) break;
                Thread.Sleep(10);
            }
            if (Assembly.GetEntryAssembly() == null) return false;

            // 不要在这里探测 PCL 的类型：GetTypes / GetType 会触发 PCL 自己的 AssemblyResolve，
            // 在启动早期重入会把它自己的启动流程搞坏。改为监听 WPF 启动事件后再动手。
            return WaitForMainWindowLoaded(TimeSpan.FromSeconds(30));
        }

        private static bool WaitForMainWindowLoaded(TimeSpan timeout)
        {
            using (var loadedSignal = new ManualResetEventSlim(false))
            {
                var startedAt = DateTime.UtcNow;
                var handlerBridge = new LoadedHandlerBridge(loadedSignal);
                bool HookWpf(Assembly assembly)
                {
                    try
                    {
                        if (!string.Equals(assembly.GetName().Name, "PresentationFramework", StringComparison.Ordinal))
                            return false;

                        var windowType = assembly.GetType("System.Windows.Window", false);
                        var frameworkElementType = assembly.GetType("System.Windows.FrameworkElement", false);
                        if (windowType == null || frameworkElementType == null) return false;

                        var loadedEvent = windowType.GetEvent("Loaded", BindingFlags.Instance | BindingFlags.Public);
                        var loadedEventField = frameworkElementType.GetField("LoadedEvent", BindingFlags.Public | BindingFlags.Static);
                        if (loadedEvent == null || loadedEventField == null) return false;

                        var loadedHandlerType = loadedEvent.EventHandlerType;
                        if (loadedHandlerType == null) return false;

                        var loadedHandlerMethod = typeof(LoadedHandlerBridge).GetMethod(nameof(LoadedHandlerBridge.OnLoaded));
                        if (loadedHandlerMethod == null) return false;
                        var loadedHandler = Delegate.CreateDelegate(loadedHandlerType, handlerBridge, loadedHandlerMethod, false);
                        if (loadedHandler == null) return false;

                        if (TryHookMainWindowLoaded(assembly, loadedHandler))
                            return true;

                        var routedEvent = loadedEventField.GetValue(null);
                        if (routedEvent == null) return false;

                        var eventManagerType = AppDomain.CurrentDomain
                            .GetAssemblies()
                            .Select(x => x.GetType("System.Windows.EventManager", false))
                            .FirstOrDefault(x => x != null);
                        if (eventManagerType == null) return false;

                        var registerClassHandler = eventManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .FirstOrDefault(m =>
                            {
                                if (m.Name != "RegisterClassHandler") return false;
                                var ps = m.GetParameters();
                                return ps.Length >= 3 &&
                                       ps[0].ParameterType == typeof(Type) &&
                                       typeof(Delegate).IsAssignableFrom(ps[2].ParameterType);
                            });
                        if (registerClassHandler == null) return false;

                        var parameters = registerClassHandler.GetParameters();
                        object[] args;
                        if (parameters.Length >= 4 && parameters[3].ParameterType == typeof(bool))
                            args = new[] { windowType, routedEvent, loadedHandler, false };
                        else
                            args = new[] { windowType, routedEvent, loadedHandler };

                        registerClassHandler.Invoke(null, args);
                        Log.Info("PatchHost: 已挂钩 Window.Loaded 事件");
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log.Info("PatchHost: 挂钩 Loaded 事件失败: " + ex.GetType().Name + ": " + ex.Message);
                        return false;
                    }
                }

                void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
                {
                    if (HookWpf(e.LoadedAssembly))
                        loadedSignal.Set();
                }

                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (HookWpf(asm))
                        {
                            loadedSignal.Set();
                            break;
                        }
                    }

                    var remaining = timeout - (DateTime.UtcNow - startedAt);
                    if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                    if (!loadedSignal.Wait(remaining))
                    {
                        Log.Info("PatchHost: Window.Loaded 未在超时时间内触发");
                        return false;
                    }
                    return true;
                }
                finally
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                }
            }
        }

        private static bool TryHookMainWindowLoaded(Assembly presentationFramework, Delegate loadedHandler)
        {
            var appType = presentationFramework.GetType("System.Windows.Application", false);
            if (appType == null) return false;

            var current = appType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
            if (current == null) return false;

            var mainWindow = appType.GetProperty("MainWindow", BindingFlags.Public | BindingFlags.Instance)?.GetValue(current, null);
            if (mainWindow == null) return false;

            var isLoadedProp = mainWindow.GetType().GetProperty("IsLoaded", BindingFlags.Public | BindingFlags.Instance);
            if (isLoadedProp != null && isLoadedProp.PropertyType == typeof(bool))
            {
                var loaded = (bool)isLoadedProp.GetValue(mainWindow, null);
                if (loaded)
                {
                    Log.Info("PatchHost: MainWindow 已加载完成");
                    return true;
                }
            }

            var loadedEvent = mainWindow.GetType().GetEvent("Loaded", BindingFlags.Instance | BindingFlags.Public);
            if (loadedEvent == null) return false;
            loadedEvent.AddEventHandler(mainWindow, loadedHandler);
            Log.Info("PatchHost: 已挂钩 MainWindow.Loaded");
            return false;
        }

        private sealed class LoadedHandlerBridge
        {
            private readonly ManualResetEventSlim _loadedSignal;
            public LoadedHandlerBridge(ManualResetEventSlim loadedSignal) => _loadedSignal = loadedSignal;
            public void OnLoaded(object sender, object args) => _loadedSignal.Set();
        }
    }
}
