using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PclRuntime
{
    /// <summary>
    /// 5 个补丁的 Harmony 版本，对应 tools/PclPatch/Patcher.cs 里的 Cecil 离线补丁。
    /// 1/2/3/5 用 transpiler 改 IL；4 因为原方法体没有合法出口（连 ret 都没有），
    /// 无法被 Harmony 复制成 DynamicMethod，改为在 5 个调用点做重定向。
    /// </summary>
    internal static class Patches
    {
        private static readonly Type[] ThreeStrings = { typeof(string), typeof(string), typeof(string) };

        public static void ApplyAll(Harmony harmony)
        {
            var only = Environment.GetEnvironmentVariable("PCLRT_ONLY");
            bool Want(string id) => string.IsNullOrEmpty(only) || only.Contains(id);

            if (Want("1"))
                Apply(harmony, "1", "PCL.PageDownloadInstall", "LoaderInit", null, "Transpiler1");

            if (Want("2"))
                Apply(harmony, "2", "PCL.PageDownloadModify", "LoadInstanceFinished", null, "Transpiler2");

            if (Want("3"))
                Apply(harmony, "3", "PCL.PageDownloadResourceDetail", "InitializeStackResourceVersions", null, "Transpiler3");

            // 4: ModpackInstall(File, InstanceName, Logo) 的 5 个调用点
            if (Want("4"))
                foreach (var site in new[]
                         {
                             ("PCL.PageLaunchLeft", "_Lambda$__4-0"),
                             ("PCL.ModModpack+_Closure$__0-0", "_Lambda$__0"),
                             ("PCL.ModModpack+_Closure$__10-0", "_Lambda$__0"),
                             ("PCL.PageDownloadResourceDetail+_Closure$__22-0", "_Lambda$__0"),
                             ("PCL.FormMain+_Closure$__25-0", "_Lambda$__0"),
                         })
                    Apply(harmony, "4", site.Item1, site.Item2, null, "Transpiler4");

            // 5: ModpackInstall()
            if (Want("5"))
                Apply(harmony, "5", "PCL.ModModpack", "ModpackInstall", Type.EmptyTypes, "Transpiler5");
        }

        private static void Apply(Harmony harmony, string id, string typeName, string methodName,
                                  Type[] args, string transpiler)
        {
            try
            {
                var original = Resolve(typeName, methodName, args);
                if (original == null)
                {
                    Log.Info($"[{id}] 目标方法未找到: {typeName}.{methodName}");
                    return;
                }

                harmony.Patch(original, transpiler: new HarmonyMethod(Find(transpiler)));
                Log.Info($"[{id}] patched {typeName}.{methodName} ({original.MetadataToken:X8})");
            }
            catch (Exception ex)
            {
                Log.Info($"[{id}] FAILED {typeName}.{methodName}: {ex}");
            }
        }

        private static MethodInfo Resolve(string typeName, string methodName, Type[] args)
        {
            var type = Assembly.GetEntryAssembly()?.GetType(typeName, false);
            if (type == null)
            {
                Log.Info($"  type not found: {typeName}");
                return null;
            }
            var method = args == null
                ? type.GetMethod(methodName, AccessTools.all)
                : type.GetMethod(methodName, AccessTools.all, null, args, null);
            if (method == null)
            {
                var names = type.GetMethods(AccessTools.all)
                    .Where(m => m.Name.Contains("Lambda"))
                    .Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
                Log.Info($"  method not found: {typeName}.{methodName}; candidates: {string.Join(" | ", names)}");
            }
            return method;
        }

        private static MethodInfo Find(string name) =>
            typeof(Patches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Patches), name);

        private static string MemberName(object operand) =>
            operand is MethodBase mb ? mb.Name : operand is FieldInfo fi ? fi.Name : null;

        private static CodeInstruction Move(CodeInstruction from, CodeInstruction to)
        {
            to.labels.AddRange(from.labels);
            to.blocks.AddRange(from.blocks);
            return to;
        }

        // ------------------------------------------------------------------ (1) 下载页 LoaderTask
        // PageDownloadInstall.LoaderInit: 把“关闭下载”的 LoaderTask 换成 ModDownload.DlClientListLoader。
        private static IEnumerable<CodeInstruction> Transpiler1(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();

            int strIdx = list.FindIndex(i => i.opcode == OpCodes.Ldstr && (i.operand as string) == "关闭下载");
            if (strIdx < 0) throw new InvalidOperationException("ldstr 关闭下载 not found");

            int endIdx = -1;
            for (int i = strIdx + 1; i < list.Count; i++)
                if (list[i].opcode == OpCodes.Ldarg_0) { endIdx = i; break; }
            if (endIdx < 0) throw new InvalidOperationException("ldarg.0 not found");

            var field = AccessTools.Field("PCL.ModDownload:DlClientListLoader")
                        ?? throw new MissingFieldException("PCL.ModDownload", "DlClientListLoader");

            var repl = new CodeInstruction(OpCodes.Ldsfld, field);
            for (int i = strIdx; i < endIdx; i++)
            {
                repl.labels.AddRange(list[i].labels);
                repl.blocks.AddRange(list[i].blocks);
            }
            list.RemoveRange(strIdx, endIdx - strIdx);
            list.Insert(strIdx, repl);

            return list;
        }

        // ------------------------------------------------------------------ (2) 修改页 MC 卡片
        // PageDownloadModify.LoadInstanceFinished: CardMinecraft.Visibility = Collapsed -> Visible
        private static IEnumerable<CodeInstruction> Transpiler2(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (int i = 0; i + 2 < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Callvirt && MemberName(list[i].operand) == "get_CardMinecraft" &&
                    list[i + 1].opcode == OpCodes.Ldc_I4_2 &&
                    list[i + 2].opcode == OpCodes.Callvirt && MemberName(list[i + 2].operand) == "set_Visibility")
                {
                    list[i + 1] = Move(list[i + 1], new CodeInstruction(OpCodes.Ldc_I4_0));
                    return list;
                }
            }
            throw new InvalidOperationException("CardMinecraft.Visibility pattern not found");
        }

        // ------------------------------------------------------------------ (3) 资源详情安装按钮
        // InitializeStackResourceVersions: 恢复整合包分支的 Install 按钮。
        // 原：ldarg.0; ldftn Save_Click; newobj ClickEventHandler; ldnull; ldloc.1; ToListItem; Add
        private static IEnumerable<CodeInstruction> Transpiler3(IEnumerable<CodeInstruction> instructions)
        {
            if (Environment.GetEnvironmentVariable("PCLRT_ID3") == "1") return instructions;
            var list = instructions.ToList();

            int i = list.FindIndex(x => x.opcode == OpCodes.Callvirt && MemberName(x.operand) == "ToListItem");
            if (i < 5) throw new InvalidOperationException("ToListItem not found");

            var ldarg0 = list[i - 5];   // ldarg.0
            var ldftn = list[i - 4];    // ldftn Save_Click
            var newobj = list[i - 3];   // newobj MyListItem/ClickEventHandler

            if (ldarg0.opcode != OpCodes.Ldarg_0 || ldftn.opcode != OpCodes.Ldftn || newobj.opcode != OpCodes.Newobj)
                throw new InvalidOperationException(
                    $"unexpected shape around ToListItem: {ldarg0.opcode} {ldftn.opcode} {newobj.opcode}");

            var targetField = AccessTools.Field("PCL.PageDownloadResourceDetail:TargetResourceType")
                              ?? throw new MissingFieldException("TargetResourceType");
            var saveClick = AccessTools.Method("PCL.PageDownloadResourceDetail:Save_Click")
                            ?? throw new MissingMethodException("Save_Click");
            var installClick = AccessTools.Method("PCL.PageDownloadResourceDetail:_Lambda$__R18-5")
                               ?? AccessTools.Method("PCL.PageDownloadResourceDetail:Install_Click")
                               ?? throw new MissingMethodException("Install_Click");

            var handlerCtor = (ConstructorInfo)newobj.operand;
            var iconHandlerType = AccessTools.TypeByName("PCL.MyIconButton+ClickEventHandler")
                                  ?? throw new TypeLoadException("PCL.MyIconButton/ClickEventHandler");
            var iconCtor = iconHandlerType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 2)
                           ?? throw new MissingMethodException("ClickEventHandler..ctor");

            Log.Info($"  [3] installClick={installClick.DeclaringType?.Name}.{installClick.Name}" +
                     $"({string.Join(",", installClick.GetParameters().Select(p => p.ParameterType.Name))})" +
                     $" saveClick={saveClick.Name}({string.Join(",", saveClick.GetParameters().Select(p => p.ParameterType.Name))})" +
                     $" handler={handlerCtor.DeclaringType?.Name} icon={iconCtor.DeclaringType?.Name}" +
                     $" target={targetField.FieldType.Name}");

            var pick = AccessTools.Method(typeof(Patches), nameof(PickDelegate));

            // 不引入新分支/标签（Harmony 重发这套 IL 时分支标签会让 JIT 判定非法），
            // 用两次 PickDelegate 在三元选择上等价地完成：
            //   参数1 = ModPack ? Install 适配器 : Save_Click
            //   参数2 = ModPack ? Save_Click(图标版) : null
            var block = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldftn, installClick),
                new CodeInstruction(OpCodes.Newobj, handlerCtor),

                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldftn, saveClick),
                new CodeInstruction(OpCodes.Newobj, handlerCtor),

                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, targetField),
                new CodeInstruction(OpCodes.Ldc_I4_2),
                new CodeInstruction(OpCodes.Ceq),
                new CodeInstruction(OpCodes.Call, pick),
                new CodeInstruction(OpCodes.Castclass, handlerCtor.DeclaringType),

                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldftn, saveClick),
                new CodeInstruction(OpCodes.Newobj, iconCtor),

                new CodeInstruction(OpCodes.Ldnull),

                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, targetField),
                new CodeInstruction(OpCodes.Ldc_I4_2),
                new CodeInstruction(OpCodes.Ceq),
                new CodeInstruction(OpCodes.Call, pick),
                new CodeInstruction(OpCodes.Castclass, iconCtor.DeclaringType),
            };

            block[0].labels.AddRange(ldarg0.labels);
            block[0].blocks.AddRange(ldarg0.blocks);

            list.RemoveRange(i - 5, 4);
            list.InsertRange(i - 5, block);

            return list;
        }

        /// <summary>三元选择的 IL 友好版本：避免在 transpiler 里引入分支标签。</summary>
        public static object PickDelegate(object whenTrue, object whenFalse, bool useTrue) =>
            useTrue ? whenTrue : whenFalse;

        // ------------------------------------------------------------------ (4) 整合包安装调用点重定向
        // 把 call ModpackInstall(File, InstanceName, Logo) 换成 call ModpackInstaller.Install(...) + ldnull，
        // 返回槽用 null 占位（5 个调用点都是紧接着 pop）。
        private static IEnumerable<CodeInstruction> Transpiler4(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var install = AccessTools.Method(typeof(ModpackInstaller), nameof(ModpackInstaller.Install));
            int count = 0;

            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i].opcode == OpCodes.Call || list[i].opcode == OpCodes.Callvirt) &&
                    list[i].operand is MethodInfo mi &&
                    mi.Name == "ModpackInstall" &&
                    mi.DeclaringType?.Name == "ModModpack" &&
                    mi.GetParameters().Length == 3)
                {
                    list[i] = Move(list[i], new CodeInstruction(OpCodes.Call, install));
                    list.Insert(i + 1, new CodeInstruction(OpCodes.Ldnull));
                    i++;
                    count++;
                }
            }

            if (count == 0) throw new InvalidOperationException("ModpackInstall(3) call site not found");
            return list;
        }

        // ------------------------------------------------------------------ (5) 公开的 ModpackInstall()
        // 原方法体只剩一句“不再提供 MC 下载”的 Hint，直接整段替换成真正的文件选择流程。
        private static IEnumerable<CodeInstruction> Transpiler5(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var call = new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(ModpackInstaller), nameof(ModpackInstaller.ShowPicker)));
            if (list.Count > 0)
            {
                call.labels.AddRange(list[0].labels);
                call.blocks.AddRange(list[0].blocks);
            }
            return new List<CodeInstruction> { call, new CodeInstruction(OpCodes.Ret) };
        }
    }
}
