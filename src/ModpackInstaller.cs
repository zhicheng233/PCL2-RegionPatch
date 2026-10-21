using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace PclRuntime
{
    /// <summary>
    /// PCL.ModModpack.ModpackInstall(File, InstanceName, Logo) 的 C# 重写。
    /// 原方法在“法律限制”守卫处无条件 throw，之后的安装代码被编译器当作不可达删掉了，
    /// 所以只能整体顶替：这里复刻它的开包 / 类型识别 / 分发逻辑。
    /// </summary>
    internal static class ModpackInstaller
    {
        private static readonly Dictionary<string, MethodInfo> Installers = new Dictionary<string, MethodInfo>();
        private static MethodInfo _openZip;
        private static MethodInfo _deserializeJson;
        private static MethodInfo _getDisplay;
        private static PropertyInfo _mcFolder;

        // ---------------------------------------------------------------- 入口

        /// <summary>补丁 5：公开的 ModpackInstall() 被整段替换成这里。</summary>
        public static void ShowPicker()
        {
            try
            {
                var files = SelectFiles();
                var file = files?.FirstOrDefault();
                if (string.IsNullOrEmpty(file))
                {
                    Log.Info("[5] 未选择文件");
                    return;
                }
                Log.Info("[5] 选择整合包: " + file);
                RunInThread(() =>
                {
                    try { Install(file, null, null); }
                    catch (Exception ex) { ReportError(ex, "手动安装整合包失败"); }
                });
            }
            catch (Exception ex)
            {
                Log.Info("[5] ShowPicker failed: " + ex);
            }
        }

        /// <summary>补丁 4：ModpackInstall(File, InstanceName, Logo) 的实际实现。</summary>
        public static void Install(string file, string instanceName, string logo)
        {
            Log.Info($"[4] Install file={file} instance={instanceName} logo={logo}");

            var mcFolder = (string)McFolderProperty().GetValue(null, null);
            var targetFolder = string.Format("{0}versions\\{1}\\", mcFolder, instanceName);
            if (targetFolder.Contains("!") || targetFolder.Contains(";"))
                throw new OperationCanceledException("游戏路径中不能含有感叹号或分号！" + targetFolder);

            object archive = null;
            try
            {
                var baseFolder = "";
                int packType;
                try
                {
                    archive = OpenZip(file);
                    packType = Detect(archive as ZipArchive, ref baseFolder);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    throw WrapOpenError(file, ex);
                }

                Log.Info($"[4] packType={packType} baseFolder='{baseFolder}'");
                Dispatch(packType, file, archive, baseFolder, instanceName, logo);
            }
            finally
            {
                (archive as IDisposable)?.Dispose();
            }
        }

        // ---------------------------------------------------------------- 压缩包类型识别

        private static int Detect(ZipArchive zip, ref string baseFolder)
        {
            if (zip == null) throw new ArgumentNullException(nameof(zip));

            if (zip.GetEntry("mcbbs.packmeta") != null) return 3;
            if (zip.GetEntry("mmc-pack.json") != null) return 2;
            if (zip.GetEntry("modrinth.index.json") != null) return 4;

            var manifest = zip.GetEntry("manifest.json");
            if (manifest != null) return HasAddons(manifest) ? 3 : 0;

            if (zip.GetEntry("modpack.json") != null) return 1;
            if (zip.GetEntry("modpack.zip") != null || zip.GetEntry("modpack.mrpack") != null) return 9;

            foreach (var entry in zip.Entries)
            {
                var parts = entry.FullName.Split('/');
                if (parts.Length == 0) continue;
                baseFolder = parts[0] + "/";
                if (parts.Length != 2) continue;

                switch (parts[1])
                {
                    case "mcbbs.packmeta": return 3;
                    case "mmc-pack.json": return 2;
                    case "modrinth.index.json": return 4;
                    case "manifest.json":
                        if (HasAddons(entry)) { baseFolder = "overrides/"; return 3; }
                        return 0;
                    case "modpack.json": return 1;
                    case "modpack.zip":
                    case "modpack.mrpack":
                        return 9;
                }
            }

            return -1;
        }

        private static bool HasAddons(ZipArchiveEntry entry)
        {
            string text;
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                text = reader.ReadToEnd();

            var json = DeserializeJson(text);
            if (json == null) return false;

            var indexer = json.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => x.Name == "get_Item" && x.GetParameters().Length == 1 &&
                                     x.GetParameters()[0].ParameterType == typeof(string));
            return indexer?.Invoke(json, new object[] { "addons" }) != null;
        }

        private static object DeserializeJson(string text)
        {
            _deserializeJson ??= AccessTools.Method("MeloongCore.Extensions.StringExtensions:DeserializeJson")
                                 ?? throw new MissingMethodException("DeserializeJson");
            return _deserializeJson.Invoke(null, new object[] { text });
        }

        // ---------------------------------------------------------------- 分发

        private static object Dispatch(int packType, string file, object archive, string baseFolder,
                                       string instanceName, string logo)
        {
            switch (packType)
            {
                case 0: return CallInstaller("InstallPackCurseForge", file, archive, baseFolder, instanceName, logo);
                case 1: return CallInstaller("InstallPackHMCL", file, archive, baseFolder);
                case 2: return CallInstaller("InstallPackMMC", file, archive, baseFolder);
                case 3: return CallInstaller("InstallPackMCBBS", file, archive, baseFolder, instanceName);
                case 4: return CallInstaller("InstallPackModrinth", file, archive, baseFolder, instanceName, logo);
                case 9: return CallInstaller("InstallPackLauncherPack", file, archive, baseFolder);
                default: return CallInstaller("InstallPackCompress", file, archive);
            }
        }

        private static object CallInstaller(string name, params object[] args)
        {
            if (!Installers.TryGetValue(name, out var method))
            {
                method = AccessTools.Method("PCL.ModModpack:" + name)
                         ?? throw new MissingMethodException("PCL.ModModpack", name);
                Installers[name] = method;
            }

            try
            {
                return method.Invoke(null, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        // ---------------------------------------------------------------- 异常包装

        private static Exception WrapOpenError(string file, Exception inner)
        {
            try
            {
                _getDisplay ??= AccessTools.Method("MeloongCore.Extensions.ExceptionExtensions:GetDisplay");
                var display = _getDisplay?.Invoke(null, new object[] { inner, true }) as string ?? "";
                if (display.Contains("Error.WinIOError"))
                    return new Exception("整合包文件失败", inner);
            }
            catch
            {
                // 包装失败就退回到通用错误
            }

            try
            {
                var getExtension = AccessTools.Method("MeloongCore.PathUtils:GetExtension");
                if (getExtension != null && (string)getExtension.Invoke(null, new object[] { file }) == "rar")
                    return new Exception("PCL 无法处理 rar 格式的压缩包，请在解压后重新压缩为 zip 格式！", inner);
            }
            catch
            {
                // 忽略
            }

            return new Exception("整合包文件失败，文件可能损坏或为不支持的压缩格式", inner);
        }

        private static void ReportError(Exception ex, string message)
        {
            Log.Info($"[4] {message}: {ex}");
            try
            {
                var logger = AccessTools.TypeByName("MeloongCore.Logger");
                var error = logger?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Error" && m.GetParameters().Length == 4 &&
                                         m.GetParameters()[0].ParameterType == typeof(Exception));
                error?.Invoke(null, new object[] { ex, message, 0, "PclRuntime" });
            }
            catch
            {
                // 忽略
            }
        }

        // ---------------------------------------------------------------- PCL 接口

        private static List<string> SelectFiles()
        {
            var selectFile = AccessTools.Method("MeloongCore.Wpf.Dialogs:SelectFile")
                             ?? throw new MissingMethodException("SelectFile");
            var parameters = selectFile.GetParameters();
            if (parameters.Length != 4) throw new MissingMethodException("SelectFile(4)");

            object filter = null;
            try { filter = BuildFilter(parameters[3].ParameterType); }
            catch (Exception ex) { Log.Info("[5] 构建过滤器失败，改为不过滤: " + ex.Message); }

            return (List<string>)selectFile.Invoke(null,
                new object[] { "选择整合包压缩文件", false, null, filter });
        }

        private static object BuildFilter(Type nullableType)
        {
            if (!nullableType.IsGenericType) return null;

            var oneOf = nullableType.GetGenericArguments()[0];       // OneOf<IEnumerable<ValueTuple<String[],String>>, String>
            var enumType = oneOf.GetGenericArguments()[0];           // IEnumerable<ValueTuple<String[],String>>
            var tupleType = enumType.GetGenericArguments()[0];       // ValueTuple<String[],String>

            var tuple = Activator.CreateInstance(tupleType,
                new object[] { new[] { "rar", "zip", "mrpack" }, "整合包文件" });

            var array = Array.CreateInstance(tupleType, 1);
            array.SetValue(tuple, 0);

            var implicitOp = oneOf.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "op_Implicit" && m.GetParameters().Length == 1 &&
                                     m.GetParameters()[0].ParameterType == enumType)
                ?? throw new MissingMethodException(oneOf.Name, "op_Implicit");

            var oneOfValue = implicitOp.Invoke(null, new object[] { array });

            try
            {
                var ctor = nullableType.GetConstructor(new[] { oneOf });
                if (ctor != null) return ctor.Invoke(new[] { oneOfValue });
            }
            catch
            {
                // Nullable 包装失败时直接传 boxed OneOf，反射同样接受
            }
            return oneOfValue;
        }

        private static void RunInThread(Action action)
        {
            var runInThread = AccessTools.Method("PCL.ModBase:RunInThread", new[] { typeof(Action) });
            if (runInThread == null) { action(); return; }
            runInThread.Invoke(null, new object[] { action });
        }

        private static PropertyInfo McFolderProperty()
        {
            if (_mcFolder != null) return _mcFolder;
            var type = AccessTools.TypeByName("PCL.ModMinecraft")
                       ?? throw new TypeLoadException("PCL.ModMinecraft");
            return _mcFolder = type.GetProperty("McFolderSelected", BindingFlags.Public | BindingFlags.Static)
                               ?? throw new MissingMemberException("McFolderSelected");
        }

        private static ZipArchive OpenZip(string file)
        {
            _openZip ??= AccessTools.Method("MeloongCore.FileUtils:OpenZip")
                         ?? throw new MissingMethodException("OpenZip");
            return (ZipArchive)_openZip.Invoke(null, new object[] { file, CancellationToken.None });
        }
    }
}
