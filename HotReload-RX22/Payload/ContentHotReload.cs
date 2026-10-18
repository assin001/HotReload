// SPDX-License-Identifier: GPL-3.0-only
// HotReload - Koikatu runtime zipmod hot reload
// Copyright (C) 2026 As-sin
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 3 as
// published by the Free Software Foundation. See LICENSE for details.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace HotReload.Payload
{
    /// <summary>
    /// 内容包热重载核心。
    /// 由界面按 guid 驱动：释放 -> 覆盖文件 -> 重载。
    /// </summary>
    internal static class ContentHotReload
    {
        static ManualLogSource _log;

        static readonly List<string> _logs = new List<string>();
        static readonly List<string> _activeZips = new List<string>();

        static object _chaListControl;

        public static List<string> Logs { get { return _logs; } }
        public static int LogCount { get { return _logs.Count; } }

        /// <summary>由 PayloadBehaviour 注入，用来跑延迟恢复用的协程。</summary>
        public static Func<IEnumerator, Coroutine> CoroutineStarter;

        public static void Initialize(ManualLogSource log)
        {
            _log = log;

            SideloaderBridge.Initialize();
            if (!SideloaderBridge.Available)
                SayErr("Sideloader 桥接不可用: " + SideloaderBridge.LastError);
            else
                Say("就绪（" + SideloaderBridge.Capabilities + "）· build " + BuildStamp.Value
                    + "。Ctrl+F7 开关窗口；填 guid -> 释放 -> 覆盖 unity3d -> 重载");
        }

        public static void ClearLog()
        {
            _logs.Clear();
        }

        public static void ExportLog()
        {
            try
            {
                var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "hotreload");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "hotreload-log.txt");
                File.WriteAllLines(file, _logs.ToArray());
                Say("日志已导出: " + file);
            }
            catch (Exception e)
            {
                SayErr("导出失败: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ 释放

        public static void Release(string guidText)
        {
            if (!SideloaderBridge.Available) { SayErr("Sideloader 桥接不可用"); return; }

            var guids = SplitGuids(guidText);
            if (guids.Count == 0) { SayErr("请先输入 guid"); return; }

            _activeZips.Clear();
            Say("开始释放 " + guids.Count + " 个 guid...");

            foreach (var guid in guids)
            {
                var path = SideloaderBridge.FindPathByGuid(guid);
                if (string.IsNullOrEmpty(path))
                {
                    SayErr(guid + " : 找不到对应的 zipmod（guid 写错了？）");
                    continue;
                }

                if (!File.Exists(path))
                {
                    SayErr(guid + " : 文件不存在 " + path);
                    continue;
                }

                string detail;
                bool released = SideloaderBridge.ReleaseArchive(path, out detail);
                if (!_activeZips.Contains(path)) _activeZips.Add(path);

                var name = Path.GetFileName(path);
                bool locked = SideloaderBridge.IsFileLocked(path);

                if (released)
                {
                    Say(name + " 已释放（" + detail + "）文件" + (locked ? "仍被占用！" : "可以覆盖"));
                }
                else if (locked)
                {
                    Say("[!] " + name + " 不在 Sideloader 打开列表里，但文件仍被占用（" + detail + "）");
                }
                else
                {
                    Say("[!] " + name + " 没有打开的句柄（" + detail + "），可以直接覆盖");
                }
            }

            Say(_activeZips.Count == 0 ? "没有可操作的文件" : "释放完成，可以覆盖了");
        }

        // ------------------------------------------------------------------ 重载

        /// <summary>是否连 list/characustom 的表格和 manifest 一起重读。默认只要 unity3d。</summary>
        public static bool ReloadLists;

        public static void Reload()
        {
            try
            {
                if (!SideloaderBridge.Available) { SayErr("Sideloader 桥接不可用"); return; }
                if (_activeZips.Count == 0) { SayErr("没有已释放的包，先点“释放”"); return; }

                var targets = _activeZips.ToArray();
                Say("开始重载 " + targets.Length + " 个包（" + (ReloadLists ? "unity3d + 表格" : "仅 unity3d") + "）...");

                // 只有需要重载表格时才去拿 ChaListControl，平时不打这层反射
                var chaList = ReloadLists ? GetChaListControl() : null;

                int succeeded = 0;
                foreach (var path in targets)
                {
                    var name = Path.GetFileName(path);
                    if (!File.Exists(path)) { SayErr(name + " 文件不存在"); continue; }

                    string report;
                    try
                    {
                        report = SideloaderBridge.ReloadArchive(path, chaList, ReloadLists);
                    }
                    catch (Exception e)
                    {
                        SayErr(name + " 重载抛异常: " + e);
                        continue;
                    }

                    // ReloadArchive 失败也返回字符串（"失败[...]"），不能一律当成功
                    if (report != null && report.StartsWith("失败", StringComparison.Ordinal))
                    {
                        SayErr(name + " -> " + report);
                        continue;
                    }

                    succeeded++;
                    Say(name + " -> " + report);
                    Say(name + (SideloaderBridge.IsFileLocked(path)
                        ? " 已被重新打开（占用中，下次覆盖前要先释放）"
                        : " 当前未占用"));
                }

                if (succeeded == 0)
                {
                    SayErr("没有包重载成功");
                    return;
                }

                int refreshed = RefreshSceneCharacters();
                RefreshMakerUi();

                Say(refreshed > 0
                    ? "重载完成：已刷新 " + refreshed + " 个角色的衣服/饰品/头发"
                    : "重载完成：当前场景没有角色，进场景或重读卡片后生效");

                if (ReloadLists) Say("表格已重新载入：切换一次分类（或重开捏人界面）就能看到新项");

                var prune = SideloaderBridge.PruneCache();
                if (!string.IsNullOrEmpty(prune)) Say(prune);
            }
            catch (Exception e)
            {
                // 任何异常都要落到日志里，否则界面上看起来就是"点了没反应"
                SayErr("重载失败: " + e);
            }
        }

        // ------------------------------------------------------------------ 刷新

        /// <summary>
        /// 换完 bundle 后，把场上角色已经实例化出来的衣服/饰品/头发按当前坐标重建一遍。
        /// 不做这一步，角色身上的 prefab 还指着旧 bundle（甚至是被卸载的），重读卡片就会掉衣服。
        /// </summary>
        static int RefreshSceneCharacters()
        {
            var chars = CollectSceneCharacters();
            int ok = 0;

            for (int i = 0; i < chars.Count; i++)
            {
                var c = chars[i];
                if (c == null) continue;
                if (RefreshCharacter(c)) ok++;
            }
            return ok;
        }

        /// <summary>同步捏人界面：列表数据变了以后让它重建一次，否则还显示旧项。</summary>
        static void RefreshMakerUi()
        {
            try
            {
                var maker = ChaCustom.CustomBase.Instance;
                if (maker == null) return;

                // CustomBase 下一帧会按这个标记重建当前界面（分类列表/下拉框都走这里）
                maker.updateCustomUI = true;
            }
            catch (Exception) { }
        }

        static List<ChaControl> CollectSceneCharacters()
        {
            var result = new List<ChaControl>();

            try
            {
                var maker = ChaCustom.CustomBase.Instance;
                if (maker != null && maker.chaCtrl != null && !result.Contains(maker.chaCtrl))
                    result.Add(maker.chaCtrl);
            }
            catch (Exception) { }

            try
            {
                var studio = Studio.Studio.Instance;
                if (studio != null && studio.dicObjectCtrl != null)
                {
                    var snapshot = new List<Studio.ObjectCtrlInfo>(studio.dicObjectCtrl.Values);
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        var oci = snapshot[i] as Studio.OCIChar;
                        if (oci != null && oci.charInfo != null && !result.Contains(oci.charInfo))
                            result.Add(oci.charInfo);
                    }
                }
            }
            catch (Exception) { }

            if (result.Count == 0)
            {
                var all = UnityEngine.Object.FindObjectsOfType<ChaControl>();
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null) result.Add(all[i]);
            }

            return result;
        }

        static bool RefreshCharacter(ChaControl c)
        {
            object me = null;
            bool scheduled = false;

            try
            {
                me = MaterialEditorBridge.Begin(c);

                // forceChange=true：按当前坐标里的 id 重新加载美术资源
                c.ChangeClothes(true);
                c.ChangeAccessory(true);
                c.ChangeHair(true);

                if (me != null && CoroutineStarter != null)
                {
                    CoroutineStarter(MaterialEditorBridge.Reapply(me));
                    scheduled = true;
                }
                return true;
            }
            catch (Exception e)
            {
                SayErr("刷新角色失败: " + c.name + " " + e.Message);
                return false;
            }
            finally
            {
                if (!scheduled) MaterialEditorBridge.End(me);
            }
        }

        /// <summary>MaterialEditor 兼容桥（反射，没装就整体跳过）。</summary>
        static class MaterialEditorBridge
        {
            static Type _pluginType;

            public static object Begin(ChaControl c)
            {
                try
                {
                    if (_pluginType == null)
                    {
                        var asms = AppDomain.CurrentDomain.GetAssemblies();
                        for (int i = 0; i < asms.Length; i++)
                        {
                            var t = asms[i].GetType("KK_Plugins.MaterialEditor.MaterialEditorPlugin", false);
                            if (t != null) { _pluginType = t; break; }
                        }
                    }
                    if (_pluginType == null) return null;

                    var get = _pluginType.GetMethod("GetCharaController",
                        BindingFlags.Public | BindingFlags.Static);
                    if (get == null) return null;

                    var ctrl = get.Invoke(null, new object[] { c });
                    if (ctrl == null) return null;

                    // ClothesChanging 必须一起置位，否则 ChangeClothes 会被 ME 当成"玩家换装"而清掉该槽修改
                    SetFlag(ctrl, "CoordinateChanging", true);
                    SetFlag(ctrl, "CharacterLoading", true);
                    SetFlag(ctrl, "ClothesChanging", true);
                    return ctrl;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            public static void End(object ctrl)
            {
                if (ctrl == null) return;
                try
                {
                    SetFlag(ctrl, "CoordinateChanging", false);
                    SetFlag(ctrl, "CharacterLoading", false);
                    SetFlag(ctrl, "ClothesChanging", false);
                }
                catch (Exception) { }
            }

            public static IEnumerator Reapply(object ctrl)
            {
                yield return null;

                try
                {
                    // 3.x 是 LoadData(clothes, accessories, hair)，个别版本多一个 body
                    var loadData = ctrl.GetType().GetMethod("LoadData",
                        BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { typeof(bool), typeof(bool), typeof(bool) }, null);

                    if (loadData == null)
                        loadData = ctrl.GetType().GetMethod("LoadData",
                            BindingFlags.Public | BindingFlags.Instance, null,
                            new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool) }, null);

                    if (loadData != null)
                    {
                        var ps = loadData.GetParameters();
                        var args = new object[ps.Length];
                        for (int i = 0; i < args.Length; i++) args[i] = i < 3;   // 前三个 true

                        var it = loadData.Invoke(ctrl, args) as IEnumerator;
                        while (it != null)
                        {
                            bool moved;
                            try { moved = it.MoveNext(); }
                            catch (Exception e)
                            {
                                SayErr("MaterialEditor 恢复失败: " + e.Message);
                                break;
                            }
                            if (!moved) break;
                            yield return it.Current;
                        }
                    }
                }
                finally
                {
                    End(ctrl);
                }
            }

            static void SetFlag(object ctrl, string name, bool value)
            {
                var p = ctrl.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite) p.SetValue(ctrl, value, null);
            }
        }

        // ------------------------------------------------------------------ 工具

        static List<string> SplitGuids(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            var parts = text.Split(new[] { ',', '，', ';', '；', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var g = p.Trim();
                if (g.Length > 0 && !result.Contains(g)) result.Add(g);
            }
            return result;
        }

        static object GetChaListControl()
        {
            if (_chaListControl != null) return _chaListControl;

            try
            {
                // Manager.Character : Singleton<Manager.Character>，实例上有 chaListCtrl
                var managerType = SideloaderBridge.FindType("Manager.Character");
                if (managerType == null) { SayErr("找不到 Manager.Character"); return null; }

                object manager = null;
                var singletonOpen = SideloaderBridge.FindType("Singleton`1");
                if (singletonOpen != null)
                {
                    var singleton = singletonOpen.MakeGenericType(managerType);
                    var inst = singleton.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (inst != null) manager = inst.GetValue(null, null);
                }

                if (manager == null) manager = UnityEngine.Object.FindObjectOfType(managerType);
                if (manager == null) { SayErr("拿不到 Manager.Character 实例"); return null; }

                var prop = managerType.GetProperty("chaListCtrl",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (prop == null) { SayErr("Manager.Character 上没有 chaListCtrl"); return null; }

                _chaListControl = prop.GetValue(manager, null);
                if (_chaListControl == null) SayErr("chaListCtrl 为 null");
            }
            catch (Exception e)
            {
                SayErr("获取 ChaListControl 失败: " + e);
            }

            return _chaListControl;
        }

        // ------------------------------------------------------------------ 输出

        public static void Say(string msg)
        {
            Append("[i] " + msg);
            if (_log != null) _log.LogInfo(msg);
        }

        public static void SayErr(string msg)
        {
            Append("[x] " + msg);
            if (_log != null) _log.LogError(msg);
        }

        static void Append(string line)
        {
            _logs.Add(line);
            if (_logs.Count > 300) _logs.RemoveRange(0, _logs.Count - 300);
        }
    }
}
