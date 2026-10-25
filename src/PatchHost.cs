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

                if (!WaitForPcl()) { Log.Info("PatchHost: PCL 未在超时时间内就绪，放弃"); return; }

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
            // 在启动早期重入会把它自己的启动流程搞坏。等 WPF 主窗口真正 Loaded 之后再动手。
            //
            // 慢速机器上窗口创建可能远超 30s（磁盘/杀软/低配 CPU），这里给足预算。
            return WaitForMainWindowLoaded(TimeSpan.FromSeconds(60));
        }

        /// <summary>
        /// 轮询等待 WPF 主窗口 Loaded。
        ///
        /// 旧实现在 AssemblyLoad("PresentationFramework") 事件里直接反射挂钩：那时程序集刚被映射，
        /// WPF 的静态构造、Application、EventManager 全局状态都可能还没初始化完，而且这段代码运行在
        /// PatchHost 后台线程上。低配机器上正好撞进这个窗口，RegisterClassHandler 抛异常 →
        /// 被 MethodInfo.Invoke 包成 TargetInvocationException，而该失败不会重试，整次注入作废
        /// （等满超时后什么都没有，表现为"注入不上"）。
        ///
        /// 现在改为：轮询 Application.Current → 通过 Dispatcher 回到 UI 线程挂钩 → 失败就重试，
        /// 直到 Loaded 触发或超时。同时不再 Dispose 信号，避免窗口晚到后回调抛 ObjectDisposedException。
        /// </summary>
        private static bool WaitForMainWindowLoaded(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            var signal = new LoadedSignal();
            var state = new HookState();
            var bridge = new LoadedHandlerBridge(signal);

            while (DateTime.UtcNow < deadline)
            {
                if (signal.Set) return true;

                if (!state.Hooked && !state.Dispatched)
                {
                    try
                    {
                        ScheduleHookOnUiThread(bridge, state);
                    }
                    catch (Exception ex)
                    {
                        // 打完整异常（含 InnerException），否则只剩 TargetInvocationException 的壳
                        Log.Info("PatchHost: 挂钩调度失败: " + ex);
                    }
                }

                Thread.Sleep(20);
            }

            Log.Info("PatchHost: Window.Loaded 未在超时时间内触发 (hooked=" + state.Hooked +
                     ", dispatched=" + state.Dispatched + ")");
            return false;
        }

        /// <summary>
        /// 在 PatchHost 后台线程上探测是否已具备挂钩条件，条件满足则把真正的挂钩动作投递到 UI 线程。
        /// 只有 Application.Current 存在之后才投递，避免在 WPF 初始化中途动手。
        /// </summary>
        private static void ScheduleHookOnUiThread(LoadedHandlerBridge bridge, HookState state)
        {
            var pf = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "PresentationFramework", StringComparison.Ordinal));
            if (pf == null) return; // WPF 还没加载，下一轮再试

            var appType = pf.GetType("System.Windows.Application", false);
            if (appType == null) return;

            var current = appType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null, null);
            if (current == null) return; // Application 还没建出来，下一轮再试

            var dispatcher = current.GetType()
                .GetProperty("Dispatcher", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(current, null);
            if (dispatcher == null) return;

            var beginInvoke = dispatcher.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "BeginInvoke")
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault(m =>
                {
                    var ps = m.GetParameters();
                    return ps.Length >= 1 && typeof(Delegate).IsAssignableFrom(ps[0].ParameterType);
                });
            if (beginInvoke == null) return;

            state.Dispatched = true;
            var hook = new Action(() =>
            {
                try
                {
                    HookOnUiThread(pf, appType, bridge, state);
                }
                catch (Exception ex)
                {
                    Log.Info("PatchHost: UI 线程挂钩失败: " + ex);
                }
                finally
                {
                    state.Dispatched = false;
                }
            });

            // BeginInvoke(Delegate, params object[]) / BeginInvoke(Action)
            var args = beginInvoke.GetParameters().Length == 1
                ? new object[] { hook }
                : new object[] { hook, new object[0] };

            try
            {
                beginInvoke.Invoke(dispatcher, args);
            }
            catch
            {
                state.Dispatched = false;
                throw;
            }
        }

        /// <summary>
        /// 真正的挂钩，运行在 UI 线程。与 WPF 初始化同线程，因此 MainWindow / IsLoaded 的读写
        /// 不会和 Loaded 事件互相竞争。
        /// </summary>
        private static void HookOnUiThread(Assembly pf, Type appType, LoadedHandlerBridge bridge, HookState state)
        {
            if (state.Hooked || bridge.Signal.Set) return;

            var current = appType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null, null);
            if (current == null) return;

            var mainWindow = appType.GetProperty("MainWindow", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(current, null);

            if (mainWindow != null)
            {
                var isLoadedProp = mainWindow.GetType().GetProperty("IsLoaded", BindingFlags.Public | BindingFlags.Instance);
                if (isLoadedProp != null && isLoadedProp.PropertyType == typeof(bool) &&
                    (bool)isLoadedProp.GetValue(mainWindow, null))
                {
                    Log.Info("PatchHost: MainWindow 已加载完成");
                    state.Hooked = true;
                    bridge.Signal.Set = true;
                    return;
                }

                var loadedEvent = mainWindow.GetType().GetEvent("Loaded", BindingFlags.Public | BindingFlags.Instance);
                if (loadedEvent != null)
                {
                    var handler = EnsureHandler(pf, bridge);
                    if (handler != null)
                    {
                        loadedEvent.AddEventHandler(mainWindow, handler);
                        state.Hooked = true;
                        Log.Info("PatchHost: 已挂钩 MainWindow.Loaded");
                        return;
                    }
                }
            }

            // 窗口尚未创建：在 UI 线程注册类处理器（此刻与 WPF 初始化同线程，不会竞态）。
            // 注意 MainWindow 为空意味着还没有任何 Window 被 Show，所以 Loaded 一定还没触发过。
            if (TryRegisterClassHandler(pf, bridge))
            {
                state.Hooked = true;
                Log.Info("PatchHost: 已挂钩 Window.Loaded 类处理器");
            }
        }

        private static Delegate EnsureHandler(Assembly pf, LoadedHandlerBridge bridge)
        {
            if (bridge.Handler != null) return bridge.Handler;

            var windowType = pf.GetType("System.Windows.Window", false);
            var loadedEvent = windowType?.GetEvent("Loaded", BindingFlags.Instance | BindingFlags.Public);
            var handlerType = loadedEvent?.EventHandlerType;
            if (handlerType == null) return null;

            var method = typeof(LoadedHandlerBridge).GetMethod(nameof(LoadedHandlerBridge.OnLoaded));
            if (method == null) return null;

            return bridge.Handler = Delegate.CreateDelegate(handlerType, bridge, method, false);
        }

        private static bool TryRegisterClassHandler(Assembly pf, LoadedHandlerBridge bridge)
        {
            var windowType = pf.GetType("System.Windows.Window", false);
            var frameworkElementType = pf.GetType("System.Windows.FrameworkElement", false);
            if (windowType == null || frameworkElementType == null) return false;

            var loadedEventField = frameworkElementType.GetField("LoadedEvent", BindingFlags.Public | BindingFlags.Static);
            if (loadedEventField == null) return false;

            // 访问静态字段会触发 FrameworkElement 的 .cctor（注册 Loaded 路由事件）。
            // 这里已经在 UI 线程，安全的。
            var routedEvent = loadedEventField.GetValue(null);
            if (routedEvent == null) return false;

            var eventManagerType = routedEvent.GetType().Assembly.GetType("System.Windows.EventManager", false)
                                   ?? AppDomain.CurrentDomain.GetAssemblies()
                                       .Select(a => a.GetType("System.Windows.EventManager", false))
                                       .FirstOrDefault(t => t != null);
            if (eventManagerType == null) return false;

            var register = eventManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "RegisterClassHandler") return false;
                    var ps = m.GetParameters();
                    return ps.Length >= 3 &&
                           ps[0].ParameterType == typeof(Type) &&
                           typeof(Delegate).IsAssignableFrom(ps[2].ParameterType);
                });
            if (register == null) return false;

            var handler = EnsureHandler(pf, bridge);
            if (handler == null) return false;

            var ps = register.GetParameters();
            var args = ps.Length >= 4 && ps[3].ParameterType == typeof(bool)
                ? new object[] { windowType, routedEvent, handler, false }
                : new object[] { windowType, routedEvent, handler };

            register.Invoke(null, args);
            return true;
        }

        private sealed class LoadedSignal
        {
            public volatile bool Set;
        }

        private sealed class HookState
        {
            public volatile bool Dispatched; // 已投递到 UI 线程、尚未执行完
            public volatile bool Hooked;     // 已成功挂上（实例事件或类处理器）
        }

        private sealed class LoadedHandlerBridge
        {
            public readonly LoadedSignal Signal;
            public Delegate Handler;

            public LoadedHandlerBridge(LoadedSignal signal) => Signal = signal;

            // 第二个参数用 object 承接 RoutedEventArgs（Delegate.CreateDelegate 允许参数逆变）
            public void OnLoaded(object sender, object args) => Signal.Set = true;
        }
    }
}
