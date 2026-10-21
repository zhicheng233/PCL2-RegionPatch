using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace PclRuntime
{
    /// <summary>
    /// 通过 "Plain Craft Launcher 2.exe.config" 的 appDomainManagerAssembly / appDomainManagerType
    /// 在 CLR 执行入口点之前拿到代码执行权，然后起一个后台线程去给 PCL 打 Harmony 补丁。
    /// 全程不修改 exe。
    /// </summary>
    public sealed class PclRuntimeManager : AppDomainManager
    {
        private static int _started;

        public PclRuntimeManager()
        {
            Start();
        }

        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            base.InitializeNewDomain(appDomainInfo);
            Start();
        }

        private static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            try
            {
                Log.Info("injected: BaseDirectory=" + AppContext.BaseDirectory);
                if (Environment.GetEnvironmentVariable("PCLRT_TRACE") == "1")
                    AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
                var t = new Thread(PatchHost.Run)
                {
                    IsBackground = true,
                    Name = "PclRuntime.PatchHost",
                };
                t.Start();
            }
            catch (Exception ex)
            {
                Log.Info("inject failed: " + ex);
            }
        }

        private static int _traced;
        private static readonly System.Collections.Generic.HashSet<string> Seen =
            new System.Collections.Generic.HashSet<string>();

        /// <summary>
        /// 诊断用（PCLRT_TRACE=1）：每个 (异常类型, 消息) 只记录第一次，并附带抛出点的当前调用栈。
        /// 注意 FirstChanceException 会把被 catch 吞掉的异常也报出来（例如 File.Exists 内部
        /// 对非法路径抛的 ArgumentException），所以要看栈判断是否致命。
        /// </summary>
        private static void OnFirstChance(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Interlocked.Increment(ref _traced) > 3000) return;
            try
            {
                var ex = e.Exception;
                var key = ex.GetType().FullName + "|" + ex.Message;
                bool first;
                lock (Seen) first = Seen.Add(key);
                if (!first) return;

                Log.Info("EX " + ex.GetType().Name + ": " + ex.Message);
                if (ex is ReflectionTypeLoadException rtle && rtle.LoaderExceptions != null)
                {
                    foreach (var inner in rtle.LoaderExceptions.Where(x => x != null).Take(8))
                        Log.Info("    loader: " + inner.GetType().Name + ": " + inner.Message);
                }

                var st = new System.Diagnostics.StackTrace(false);
                for (int i = 1; i < st.FrameCount && i < 14; i++)
                {
                    var f = st.GetFrame(i);
                    var m = f.GetMethod();
                    Log.Info("    " + (m != null ? m.DeclaringType?.FullName + "." + m.Name : "?") +
                             " +0x" + f.GetNativeOffset().ToString("X"));
                }
            }
            catch
            {
                // 忽略
            }
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;

        public static string Path
        {
            get
            {
                if (_path != null) return _path;
                try { _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PclRuntime.log"); }
                catch { _path = "PclRuntime.log"; }
                return _path;
            }
        }

        public static void Info(string message)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(Path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [" + Thread.CurrentThread.ManagedThreadId + "] " +
                        message + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }

        public static void Reset()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
