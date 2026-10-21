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
            // 在启动早期重入会把它自己的启动流程搞坏。等它启动完再动手。
            var delay = Environment.GetEnvironmentVariable("PCLRT_DELAY");
            Thread.Sleep(int.TryParse(delay, out var ms) ? ms : 2000);
            return true;
        }
    }
}
