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
using BepInEx.Bootstrap;
using UnityEngine;

namespace HotReload.Payload
{
    /// <summary>
    /// 反射访问 Sideloader 内部结构。全部走反射，避免对 Sideloader.dll 的编译期依赖，
    /// 也避免它的可访问性变化把编译弄挂。
    /// 关键结构（KK 版 Sideloader.dll）：
    ///   Sideloader.Sideloader.Archives            List&lt;ZipFile&gt;  实例私有（启动时打开的包都在这）
    ///   Sideloader.Sideloader.Manifests           Dictionary&lt;guid, Manifest&gt; 静态公共
    ///   Sideloader.BundleManager.Bundles          Dictionary&lt;bundle名, List&lt;LazyCustom&lt;AssetBundle&gt;&gt;&gt; 静态私有
    ///   Sideloader.ListLoader.Lists.LoadCSV(Stream) / LoadList(ChaListControl, ChaListData) 静态
    /// </summary>
    internal static class SideloaderBridge
    {
        static Type _sideloaderType;
        static object _instance;
        static FieldInfo _archivesField;
        static Type _listsType;
        static Type _bundleManagerType;
        static FieldInfo _bundlesField;
        static FieldInfo _zipArchivesField;
        static MethodInfo _addBundleLoader;
        static MethodInfo _randomizeCab;
        static MethodInfo _loadCsv;
        static MethodInfo _loadList;

        static Type _abmType;
        static MethodInfo _unloadAssetBundle;
        static FieldInfo _mainManifestField;

        static Type _manifestType;
        static FieldInfo _manifestsField;
        static MethodInfo _loadAllUnityArchives;
        static Type _zipType;

        // ---- 表格/manifest 真正刷新要用的入口（Sideloader 自己的列表与 AutoResolver 解析表）----
        static FieldInfo _loadedManifestsField;
        static FieldInfo _dictListInfoField;          // ChaListControl.dictListInfo
        static PropertyInfo _externalDataListProp;    // Lists.ExternalDataList
        static MethodInfo _setPossessNew;             // Sideloader.SetPossessNew(ChaListData)
        static Type _resolveInfoType;                 // Sideloader.AutoResolver.ResolveInfo
        static Type _resolveInfoListType;
        static PropertyInfo _riGuid, _riSlot, _riLocalSlot, _riCategoryNo;
        static PropertyInfo _loadedResolveInfosProp;  // UniversalAutoResolver.LoadedResolutionInfo
        static MethodInfo _generateResolutionInfo;
        static MethodInfo _setResolveInfos;

        public static bool Available { get; private set; }
        public static string LastError { get; private set; }

        /// <summary>启动时探测到的接口可用情况（写进日志用）。</summary>
        public static string Capabilities { get; private set; }

        public static void Initialize()
        {
            Available = false;
            LastError = null;

            _sideloaderType = FindType("Sideloader.Sideloader");
            _listsType = FindType("Sideloader.ListLoader.Lists");
            _bundleManagerType = FindType("Sideloader.BundleManager");

            if (_sideloaderType == null) { LastError = "找不到 Sideloader.Sideloader"; return; }
            if (_listsType == null) { LastError = "找不到 Sideloader.ListLoader.Lists"; return; }
            if (_bundleManagerType == null) { LastError = "找不到 Sideloader.BundleManager"; return; }

            foreach (var kv in Chainloader.PluginInfos)
            {
                var info = kv.Value;
                if (info == null || info.Instance == null) continue;
                if (info.Instance.GetType() == _sideloaderType) { _instance = info.Instance; break; }
            }
            if (_instance == null) { LastError = "Sideloader 插件实例未找到（未安装？）"; return; }

            var any = BindingFlags.Public | BindingFlags.NonPublic;
            _archivesField = _sideloaderType.GetField("Archives", any | BindingFlags.Instance);
            _bundlesField = _bundleManagerType.GetField("Bundles", any | BindingFlags.Static);
            _zipArchivesField = _sideloaderType.GetField("ZipArchives", any | BindingFlags.Static);
            _addBundleLoader = _bundleManagerType.GetMethod("AddBundleLoader", any | BindingFlags.Static);
            _randomizeCab = _bundleManagerType.GetMethod("RandomizeCAB", any | BindingFlags.Static);
            _loadCsv = _listsType.GetMethod("LoadCSV", any | BindingFlags.Static);
            _loadList = _listsType.GetMethod("LoadList", any | BindingFlags.Static,
                null, new[] { FindType("ChaListControl"), _loadCsv.ReturnType }, null);

            if (_archivesField == null) { LastError = "Sideloader.Archives 结构变化"; return; }
            if (_bundlesField == null) { LastError = "BundleManager.Bundles 结构变化"; return; }
            if (_addBundleLoader == null) { LastError = "BundleManager.AddBundleLoader 结构变化"; return; }
            if (_loadList == null) { LastError = "Lists.LoadList 结构变化"; return; }

            // AssetBundleManager 自己还缓存着 LoadedAssetBundle，不清掉的话重载后还是旧内容
            _abmType = FindType("AssetBundleManager");
            if (_abmType != null)
            {
                _unloadAssetBundle = _abmType.GetMethod("UnloadAssetBundle",
                    new[] { typeof(string), typeof(bool), typeof(string), typeof(bool) });
                _mainManifestField = _abmType.GetField("MAIN_MANIFEST_NAME", any | BindingFlags.Static);
            }

            // 重载后把 manifest 也换掉，否则卡片的 ID 解析还按旧的来
            _manifestType = FindType("Sideloader.Manifest");
            if (_manifestType != null)
                _manifestsField = _sideloaderType.GetField("Manifests", any | BindingFlags.Static);

            // zip 的类型直接取 Archives 的元素类型，保证和 Sideloader 里存的完全一致
            var archivesType = _archivesField.FieldType;
            if (archivesType.IsGenericType) archivesType = archivesType.GetGenericArguments()[0];
            _zipType = archivesType;

            // 复用 Sideloader 自己的 bundle 装载逻辑，和启动时走同一条路
            _loadAllUnityArchives = _sideloaderType.GetMethod("LoadAllUnityArchives",
                any | BindingFlags.Instance, null, new[] { _zipType, typeof(string) }, null);

            // ---- 表格 / manifest 刷新所需的额外入口 ----
            _loadedManifestsField = _sideloaderType.GetField("LoadedManifests", any | BindingFlags.Static);
            _setPossessNew = _sideloaderType.GetMethod("SetPossessNew",
                any | BindingFlags.Instance, null, new[] { _loadCsv.ReturnType }, null);

            var chaListType = FindType("ChaListControl");
            if (chaListType != null)
                _dictListInfoField = chaListType.GetField("dictListInfo", any | BindingFlags.Instance);

            _externalDataListProp = _listsType.GetProperty("ExternalDataList", any | BindingFlags.Static);

            var resolverType = FindType("Sideloader.AutoResolver.UniversalAutoResolver");
            _resolveInfoType = FindType("Sideloader.AutoResolver.ResolveInfo");
            if (resolverType != null && _resolveInfoType != null)
            {
                _resolveInfoListType = typeof(List<>).MakeGenericType(_resolveInfoType);
                _riGuid = _resolveInfoType.GetProperty("GUID");
                _riSlot = _resolveInfoType.GetProperty("Slot");
                _riLocalSlot = _resolveInfoType.GetProperty("LocalSlot");
                _riCategoryNo = _resolveInfoType.GetProperty("CategoryNo");
                _loadedResolveInfosProp = resolverType.GetProperty("LoadedResolutionInfo", any | BindingFlags.Static);
                _generateResolutionInfo = resolverType.GetMethod("GenerateResolutionInfo", any | BindingFlags.Static);
                _setResolveInfos = resolverType.GetMethod("SetResolveInfos", any | BindingFlags.Static);
            }

            Capabilities = "AddBundleLoader=" + (_addBundleLoader == null ? "缺" : "OK")
                + " RandomizeCAB=" + (_randomizeCab == null ? "缺" : "OK")
                + " LoadAllUnityArchives=" + (_loadAllUnityArchives == null ? "缺" : "OK")
                + " ABM=" + (_abmType == null ? "缺" : "OK")
                + " Manifest=" + (_manifestsField == null ? "缺" : "OK")
                + " AutoResolver=" + (_setResolveInfos == null ? "缺" : "OK")
                + " 外部表=" + (_externalDataListProp == null ? "缺" : "OK");

            Available = true;
        }

        public static Type FindType(string fullName)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                var t = asms[i].GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        // ---------------------------------------------------------------- 句柄释放

        /// <summary>把某个 zipmod 从 Sideloader 的打开列表里摘掉并关闭，文件随即解锁。</summary>
        public static bool ReleaseArchive(string zipPath, out string detail)
        {
            detail = null;
            if (!Available)
            {
                detail = "桥接不可用: " + LastError;
                return false;
            }

            var archives = _archivesField.GetValue(_instance) as IList;
            if (archives == null)
            {
                detail = "Sideloader.Archives 读取失败";
                return false;
            }

            int closed = RemoveArchiveEntries(archives, zipPath);
            detail = "打开中=" + archives.Count + " 关闭=" + closed;
            if (closed == 0)
            {
                var sample = SampleArchiveNames(archives, 3);
                if (sample.Length > 0) detail = detail + " 样本=" + sample;
            }
            return closed > 0;
        }

        /// <summary>摘掉列表里所有指向同一个文件的条目并关闭，返回关闭数量。</summary>
        static int RemoveArchiveEntries(IList archives, string zipPath)
        {
            int closed = 0;
            for (int i = archives.Count - 1; i >= 0; i--)
            {
                object zip = archives[i];
                if (!SameArchive(GetZipName(zip), zipPath)) continue;

                archives.RemoveAt(i);
                CloseZip(zip);
                closed++;
            }
            return closed;
        }

        static string SampleArchiveNames(IList archives, int max)
        {
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var item in archives)
            {
                if (n >= max) break;
                if (n > 0) sb.Append(" | ");
                sb.Append(Path.GetFileName(GetZipName(item)));
                n++;
            }
            return sb.ToString();
        }

        static bool SameArchive(string archiveName, string zipPath)
        {
            if (string.IsNullOrEmpty(archiveName) || string.IsNullOrEmpty(zipPath)) return false;

            var a = SafeFullPath(archiveName);
            var b = SafeFullPath(zipPath);
            if (a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

            // 路径写法不一致时按文件名兜底
            return string.Equals(Path.GetFileName(archiveName), Path.GetFileName(zipPath),
                StringComparison.OrdinalIgnoreCase);
        }

        static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 关闭 ZipFile。SharpZipLib 在 IsStreamOwner=false 时 Close() 不会关掉底层文件流，
        /// 会出现"已经从列表里摘掉了，但文件还是被锁着"的情况，所以先强制把流所有权要回来。
        /// </summary>
        static void CloseZip(object zip)
        {
            try
            {
                var owner = zip.GetType().GetProperty("IsStreamOwner");
                if (owner != null && owner.CanWrite && owner.PropertyType == typeof(bool))
                    owner.SetValue(zip, true, null);
            }
            catch (Exception)
            {
            }
            InvokeIfExists(zip, "Close");
        }

        /// <summary>文件是否还被占着（用独占方式打开试一下）。</summary>
        public static bool IsFileLocked(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return true; }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- 重新装载

        /// <summary>重新打开 zip 并注册它的 bundle 与 csv，返回简报（不抛异常，失败也返回原因）。</summary>
        public static string ReloadArchive(string zipPath, object chaListControl, bool reloadLists)
        {
            if (!Available) return "桥接不可用: " + LastError;
            if (!File.Exists(zipPath)) return "文件不存在";

            object zip = null;
            var steps = new System.Text.StringBuilder();
            try
            {
                var archives = _archivesField.GetValue(_instance) as IList;
                if (archives == null) return "Sideloader.Archives 读取失败";

                zip = OpenZipFor(zipPath, archives);
                if (zip == null) return "打开 zip 失败: " + LastError;

                // 类型必须和 Archives 里存的一致，否则 IList.Add 会抛 ArgumentException("item")
                var elementType = _zipType;
                if (elementType != null && !elementType.IsInstanceOfType(zip))
                {
                    CloseZip(zip);
                    return "zip 类型不匹配: " + zip.GetType().AssemblyQualifiedName
                        + " != " + elementType.AssemblyQualifiedName;
                }

                // 先只做解析 + 抽缓存，旧的注册表/句柄一律不碰：失败就整段回退（kept previous state）
                var reg = RegisterBundles(zip, zipPath);
                if (reg != null && reg.StartsWith("失败", StringComparison.Ordinal))
                {
                    try { CloseZip(zip); } catch (Exception) { }
                    return "失败[" + steps + reg + "]";
                }

                // 到这里新包已经验证通过，才替换掉同路径的旧句柄
                try { steps.Append("清旧句柄=" + RemoveArchiveEntries(archives, zipPath) + " "); }
                catch (Exception e) { steps.Append("清旧句柄异常(" + Unwrap(e).Message + ") "); }

                archives.Add(zip);
                steps.Append(reg);

                if (reloadLists)
                {
                    steps.Append("；").Append(MergeLists(zip, chaListControl));
                    steps.Append("；").Append(RefreshManifest(zip));
                }
                else
                {
                    steps.Append("；表格/manifest 跳过");
                }

                return steps.ToString();
            }
            catch (Exception e)
            {
                // 失败时把刚打开的 zip 关掉，否则文件会被一直锁着（下次覆盖就失败了）
                if (zip != null)
                {
                    try
                    {
                        var archives = _archivesField.GetValue(_instance) as IList;
                        if (archives != null) archives.Remove(zip);
                    }
                    catch (Exception)
                    {
                    }
                    CloseZip(zip);
                }

                // 再兜一遍：可能还有更早遗留的同路径句柄
                try
                {
                    var archives = _archivesField.GetValue(_instance) as IList;
                    if (archives != null) RemoveArchiveEntries(archives, zipPath);
                }
                catch (Exception)
                {
                }

                // 打完整堆栈，否则只知道 "ArgumentException item" 没法定位
                return "失败[" + steps + "]: " + Unwrap(e)
                    + "；文件" + (IsFileLocked(zipPath) ? "仍被占用" : "已解锁");
            }
        }

        /// <summary>用新 zip 里的 manifest.xml 覆盖 Manifests / LoadedManifests 里同 guid 的旧记录。</summary>
        static string RefreshManifest(object zip)
        {
            try
            {
                if (_manifestType == null || _manifestsField == null) return "manifest 跳过（接口缺失）";

                var manifest = LoadManifestFromZip(zip);
                if (manifest == null)
                    return "manifest 没读到" + (string.IsNullOrEmpty(_manifestError) ? "" : ": " + _manifestError);

                var guid = ManifestString(manifest, "GUID");
                if (string.IsNullOrEmpty(guid)) return "manifest 没有 guid";

                var manifests = _manifestsField.GetValue(null) as IDictionary;
                if (manifests != null) manifests[guid] = manifest;

                // 模组列表/缺失提示读的是 LoadedManifests，不换掉的话名字还是旧的
                var loaded = _loadedManifestsField == null ? null : _loadedManifestsField.GetValue(null) as IList;
                if (loaded != null)
                {
                    bool replaced = false;
                    for (int i = 0; i < loaded.Count; i++)
                    {
                        if (ManifestString(loaded[i], "GUID") != guid) continue;
                        loaded[i] = manifest;
                        replaced = true;
                        break;
                    }
                    if (!replaced) loaded.Add(manifest);
                }

                var name = ManifestString(manifest, "Name");
                return "manifest=" + guid + (string.IsNullOrEmpty(name) ? "" : "（" + name + "）");
            }
            catch (Exception e)
            {
                return "manifest 刷新失败: " + Unwrap(e).Message;
            }
        }

        /// <summary>从 zip 里解出 manifest.xml（没有就返回 null，原因放 _manifestError）。</summary>
        static object LoadManifestFromZip(object zip)
        {
            _manifestError = null;
            try
            {
                if (_manifestType == null) { _manifestError = "找不到 Sideloader.Manifest"; return null; }

                // 注意：TryLoadFromZip 是 internal static（不是 public），只查 Public 会拿到 null，
                // 表现出来就是"每张表都读了，但 manifest 永远没读到"。
                var tryLoad = _manifestType.GetMethod("TryLoadFromZip",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (tryLoad == null) { _manifestError = "Manifest.TryLoadFromZip 结构变化"; return null; }

                var args = new object[] { zip, null };
                var ok = (bool)tryLoad.Invoke(null, args);
                if (ok) return args[1];

                _manifestError = "TryLoadFromZip 返回 false";
                return null;
            }
            catch (Exception e)
            {
                _manifestError = Unwrap(e).Message;
                return null;
            }
        }

        static string _manifestError;

        static string ManifestString(object manifest, string property)
        {
            if (manifest == null) return null;
            try
            {
                var p = manifest.GetType().GetProperty(property);
                return p == null ? null : p.GetValue(manifest, null) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static Exception Unwrap(Exception e)
        {
            var tie = e as TargetInvocationException;
            return (tie != null && tie.InnerException != null) ? tie.InnerException : e;
        }

        static string GetManifestName()
        {
            var name = _mainManifestField == null ? null : _mainManifestField.GetValue(null) as string;
            return string.IsNullOrEmpty(name) ? "abdata" : name;
        }

        /// <summary>缓存键的两种写法（带不带 .unity3d）都试一遍。</summary>
        static IEnumerable<string> KeyVariants(string bundleName)
        {
            yield return bundleName;
            if (bundleName.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase))
                yield return bundleName.Substring(0, bundleName.Length - ".unity3d".Length);
            else
                yield return bundleName + ".unity3d";
        }

        /// <summary>
        /// 把某个 bundle 从 AssetBundleManager 的缓存里移除，让下次加载走新 zip 的内容。
        /// 用 Unload(false)：只卸载包、不销毁已经加载出来的对象，
        /// 否则场上角色/材质会瞬间变粉甚至被重置（这一步踩过坑）。
        /// </summary>
        public static bool HardUnloadBundle(string bundleName)
        {
            if (_abmType == null) return false;

            bool released = false;
            try
            {
                var manifest = GetManifestName();

                // 1) 走官方卸载：处理引用计数和依赖
                //    参数顺序是 (bundleName, isUnloadForceRefCount, manifestName, unloadAllLoadedObjects)，
                //    最后一个必须 false，否则场上角色正在用的衣服/材质会被一起销毁
                try
                {
                    var manager = GetSingletonInstance(_abmType);
                    if (manager != null && _unloadAssetBundle != null)
                        _unloadAssetBundle.Invoke(manager, new object[] { bundleName, true, manifest, false });
                }
                catch (Exception)
                {
                }

                // 2) 兜底：引用计数没归零时官方卸载会留着缓存，这里直接摘掉
                var packProp = _abmType.GetProperty("ManifestBundlePack",
                    BindingFlags.Public | BindingFlags.Static);
                var packDict = packProp == null ? null : packProp.GetValue(null, null) as IDictionary;

                object pack = null;
                if (packDict != null)
                {
                    if (packDict.Contains(manifest)) pack = packDict[manifest];
                    else
                    {
                        foreach (DictionaryEntry kv in packDict) { pack = kv.Value; break; }
                    }
                }

                if (pack != null)
                {
                    var loadedProp = pack.GetType().GetProperty("LoadedAssetBundles",
                        BindingFlags.Public | BindingFlags.Instance);
                    var loaded = loadedProp == null ? null : loadedProp.GetValue(pack, null) as IDictionary;

                    if (loaded != null)
                    {
                        foreach (var key in KeyVariants(bundleName))
                        {
                            if (!loaded.Contains(key)) continue;

                            var cached = loaded[key];
                            loaded.Remove(key);

                            var abField = cached == null ? null : cached.GetType().GetField("m_AssetBundle");
                            var ab = abField == null ? null : abField.GetValue(cached) as AssetBundle;
                            if (ab != null)
                            {
                                // false = 不销毁已加载对象，避免场上模型被抽空
                                try { ab.Unload(false); } catch (Exception) { }
                            }
                            released = true;
                        }
                    }

                    // 异步创建中的那条也要清，否则可能复用旧的 AssetBundleCreate
                    var createProp = pack.GetType().GetProperty("CreateAssetBundles",
                        BindingFlags.Public | BindingFlags.Instance);
                    var creating = createProp == null ? null : createProp.GetValue(pack, null) as IDictionary;
                    if (creating != null)
                    {
                        foreach (var key in KeyVariants(bundleName))
                            if (creating.Contains(key)) creating.Remove(key);
                    }
                }
            }
            catch (Exception)
            {
                // 任何一步失败都不该把整个重载带崩，外面还会继续换加载器
            }

            try { ForgetLoadedName(bundleName); } catch (Exception) { }
            return released;
        }

        /// <summary>把名字从 AllLoadedAssetBundleNames 里摘掉，避免下次加载被引用计数挡住。</summary>
        static void ForgetLoadedName(string bundleName)
        {
            try
            {
                var prop = _abmType.GetProperty("AllLoadedAssetBundleNames",
                    BindingFlags.Public | BindingFlags.Static);
                var names = prop == null ? null : prop.GetValue(null, null);
                if (names == null) return;

                var remove = names.GetType().GetMethod("Remove", new[] { typeof(string) });
                if (remove == null) return;

                foreach (var key in KeyVariants(bundleName))
                    remove.Invoke(names, new object[] { key });
            }
            catch (Exception)
            {
            }
        }

        static object GetSingletonInstance(Type t)
        {
            var singletonOpen = FindType("Singleton`1");
            if (singletonOpen == null) return null;

            var singleton = singletonOpen.MakeGenericType(t);
            var prop = singleton.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            return prop == null ? null : prop.GetValue(null, null);
        }

        /// <summary>
        /// Sideloader.ZipArchives 是 Dictionary&lt;string,string&gt;，方向不确定，
        /// 两边都试一下：key 或 value 等于 guid 就返回另一边。
        /// </summary>
        public static string FindPathByGuid(string guid)
        {
            if (!Available || _zipArchivesField == null) return null;

            var map = _zipArchivesField.GetValue(null) as IDictionary;
            if (map == null) return null;

            foreach (DictionaryEntry entry in map)
            {
                var k = entry.Key as string;
                var v = entry.Value as string;
                if (k == null || v == null) continue;

                if (string.Equals(k, guid, StringComparison.OrdinalIgnoreCase)) return v;
                if (string.Equals(v, guid, StringComparison.OrdinalIgnoreCase)) return k;
            }

            return null;
        }

        /// <summary>读 zip 内某个文本条目（用于解析 manifest.xml）。</summary>
        public static string ReadTextEntry(string zipPath, string entryName)
        {
            var zip = OpenZip(zipPath);
            if (zip == null) return null;

            try
            {
                var getEntry = zip.GetType().GetMethod("GetEntry", new[] { typeof(string) });
                if (getEntry == null) return null;

                var entry = getEntry.Invoke(zip, new object[] { entryName });
                if (entry == null) return null;

                var bytes = ReadEntryBytes(zip, entry);
                if (bytes == null) return null;

                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                InvokeIfExists(zip, "Close");
            }
        }

        static object OpenZip(string zipPath)
        {
            var zipType = FindType("ICSharpCode.SharpZipLib.Zip.ZipFile");
            if (zipType == null) { LastError = "找不到 SharpZipLib.ZipFile"; return null; }
            try { return Activator.CreateInstance(zipType, new object[] { zipPath }); }
            catch (Exception e) { LastError = "new ZipFile 失败: " + e.Message; return null; }
        }

        /// <summary>用 Archives 的元素类型来创建 ZipFile，避免类型不一致导致 IList.Add 失败。</summary>
        static object OpenZipFor(string zipPath, IList archives)
        {
            var t = _zipType;
            if (t == null && archives != null && archives.GetType().IsGenericType)
                t = archives.GetType().GetGenericArguments()[0];
            if (t == null) t = FindType("ICSharpCode.SharpZipLib.Zip.ZipFile");
            if (t == null) { LastError = "找不到 ZipFile 类型"; return null; }

            try { return Activator.CreateInstance(t, new object[] { zipPath }); }
            catch (Exception e) { LastError = "new ZipFile 失败: " + Unwrap(e).Message; return null; }
        }

        static string RegisterBundles(object zip, string zipPath)
        {
            var bundles = _bundlesField.GetValue(null) as IDictionary;
            if (bundles == null) return "bundle: Bundles 字段读取失败";

            // 先收集这次要换的 bundle（zip 里可能有同名条目，去重）
            var names = new List<string>();
            var entries = new List<object>();
            try
            {
                foreach (var entry in EnumerateEntries(zip))
                {
                    var entryName = GetEntryName(entry);
                    if (entryName == null) continue;
                    if (!entryName.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)) continue;

                    // abdata/chara/sdzxy.unity3d -> chara/sdzxy.unity3d
                    var bundleName = NormalizeBundleName(entryName);
                    if (names.Contains(bundleName)) continue;

                    names.Add(bundleName);
                    entries.Add(entry);
                }
            }
            catch (Exception e)
            {
                return "bundle 枚举失败: " + Unwrap(e);
            }

            if (names.Count == 0) return "bundle: zip 里没有 .unity3d";

            // 0) 先把新 bundle 抽到磁盘缓存（顺带校验内容）。失败就整段返回，旧加载器一个都不动
            var cachedPaths = new List<string>();
            for (int i = 0; i < names.Count; i++)
            {
                try { cachedPaths.Add(ExtractToCache(zip, entries[i])); }
                catch (Exception e)
                {
                    return "失败[缓存 " + names[i] + ": " + Unwrap(e).Message + "]，kept previous state";
                }
            }

            // 1) 先把旧加载器整个摘掉：它闭包着旧 ZipFile，
            //    留在列表里 TryGetObjectFromName 会先拿到它（已关闭的包），衣服就会直接消失
            int detached = DetachBundleLoaders(bundles, names);

            // 2) 再把 ABM 缓存里的旧 bundle 卸掉，否则新加载器拿到的还是旧内容
            int unloaded = 0;
            string unloadError = "";
            for (int i = 0; i < names.Count; i++)
            {
                try
                {
                    if (HardUnloadBundle(names[i])) unloaded++;
                }
                catch (Exception e)
                {
                    if (unloadError.Length == 0) unloadError = Unwrap(e).Message;
                }
            }

            // 3) 优先注册"从缓存文件加载"的委托：Unity 不再打开 zipmod，源文件随时可覆盖
            if (_addBundleLoader != null && cachedPaths.Count == names.Count)
            {
                int regOk = 0;
                string regError = null;

                for (int i = 0; i < names.Count; i++)
                {
                    try { RegisterCachedLoader(names[i], cachedPaths[i]); regOk++; }
                    catch (Exception e)
                    {
                        if (regError == null) regError = Unwrap(e).Message;
                    }
                }

                if (regOk > 0)
                {
                    return "bundle 共" + names.Count + " 缓存=" + cachedPaths.Count
                        + " 摘旧加载器=" + detached + " 卸载旧缓存=" + unloaded
                        + " 注册=缓存" + regOk + "/" + names.Count
                        + (regError == null ? "" : " 首个注册错误: " + regError)
                        + (unloadError.Length == 0 ? "" : " 卸载异常: " + unloadError)
                        + VerifyBundles(bundles, names);
                }
            }

            // 4) 兜底：复用 Sideloader 自己的装载逻辑，和游戏启动时走完全同一条路
            if (_loadAllUnityArchives != null)
            {
                try
                {
                    _loadAllUnityArchives.Invoke(_instance, new object[] { zip, zipPath });
                    return "bundle 共" + names.Count + " 摘旧加载器=" + detached + " 卸载旧缓存=" + unloaded
                        + "（Sideloader 装载）" + VerifyBundles(bundles, names)
                        + (unloadError.Length == 0 ? "" : " 卸载异常: " + unloadError);
                }
                catch (Exception e)
                {
                    if (unloadError.Length == 0)
                        unloadError = "Sideloader 装载失败: " + Unwrap(e).Message;
                }
            }

            // 4) 兜底：自己注册
            int ok = 0;
            int fail = 0;
            string firstError = null;

            for (int i = 0; i < names.Count; i++)
            {
                try
                {
                    var loader = new ZipBundleLoader(zip, entries[i]);
                    var del = Delegate.CreateDelegate(typeof(Func<AssetBundle>), loader,
                        typeof(ZipBundleLoader).GetMethod("Load")) as Func<AssetBundle>;
                    if (del == null)
                    {
                        fail++;
                        if (firstError == null) firstError = "委托创建失败 (" + names[i] + ")";
                        continue;
                    }

                    _addBundleLoader.Invoke(null, new object[] { del, names[i], null });
                    ok++;
                }
                catch (Exception e)
                {
                    fail++;
                    if (firstError == null) firstError = Unwrap(e).Message + " (" + names[i] + ")";
                }
            }

            return "bundle 共" + names.Count + " 摘旧加载器=" + detached + " 卸载旧缓存=" + unloaded
                + " 注册=" + ok + " 失败=" + fail
                + (firstError == null ? "" : " 首个错误: " + firstError)
                + (unloadError.Length == 0 ? "" : " 卸载异常: " + unloadError)
                + VerifyBundles(bundles, names);
        }

        /// <summary>
        /// 摘掉这些 bundle 的所有旧加载器。键在不同 Sideloader 版本里可能是
        /// "chara/x.unity3d" 也可能是 "charax"，所以按「去扩展名 + 去 abdata/」比。
        /// </summary>
        static int DetachBundleLoaders(IDictionary bundles, List<string> names)
        {
            if (bundles == null || names.Count == 0) return 0;

            var staleKeys = new List<object>();
            foreach (DictionaryEntry kv in bundles)
            {
                var key = kv.Key as string;
                if (key == null) continue;

                for (int i = 0; i < names.Count; i++)
                {
                    if (SameBundleKey(key, names[i])) { staleKeys.Add(kv.Key); break; }
                }
            }

            int removed = 0;
            for (int i = 0; i < staleKeys.Count; i++)
            {
                try { bundles.Remove(staleKeys[i]); removed++; } catch (Exception) { }
            }
            return removed;
        }

        static bool SameBundleKey(string key, string name)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(name)) return false;

            var a = key.Replace('\\', '/');
            var b = name.Replace('\\', '/');
            if (a.StartsWith("abdata/", StringComparison.OrdinalIgnoreCase)) a = a.Substring("abdata/".Length);
            if (b.StartsWith("abdata/", StringComparison.OrdinalIgnoreCase)) b = b.Substring("abdata/".Length);

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

            // 有的版本键里没有 .unity3d，只按上面的路径比会漏
            if (string.Equals(Path.GetFileNameWithoutExtension(a), Path.GetFileNameWithoutExtension(b),
                    StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// 注册完自己验一遍：键在不在、挂了几条加载器、能不能真的载入、载入后的 bundle 名是什么。
        /// RedirectHook 是拿 bundle.name 当键回查的，名字对不上时这里补一条别名。
        /// </summary>
        static string VerifyBundles(IDictionary bundles, List<string> names)
        {
            if (bundles == null || names.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            int max = names.Count < 3 ? names.Count : 3;

            for (int i = 0; i < max; i++)
            {
                var name = names[i];
                object listObj = null;
                int keys = 0;

                foreach (DictionaryEntry kv in bundles)
                {
                    if (!SameBundleKey(kv.Key as string, name)) continue;
                    listObj = kv.Value;
                    keys++;
                }

                sb.Append("；").Append(Path.GetFileName(name));
                if (listObj == null) { sb.Append(" 未注册"); continue; }

                var list = listObj as IList;
                sb.Append(" 键=").Append(keys).Append(" 加载器=").Append(list == null ? -1 : list.Count);
                if (list == null || list.Count == 0) continue;

                AssetBundle ab;
                var err = LoadLazyBundle(list[0], out ab);
                if (err != null) { sb.Append(" 载入异常: ").Append(err); continue; }
                if (ab == null) { sb.Append(" 载入失败(null)"); continue; }

                string assetNames;
                try
                {
                    var all = ab.GetAllAssetNames();
                    assetNames = all.Length.ToString();
                    // 带上几个资源名，方便和 zip 里 csv 写的 MainData 对照
                    int show = all.Length < 3 ? all.Length : 3;
                    for (int k = 0; k < show; k++) assetNames += (k == 0 ? " 例=" : ",") + all[k];
                }
                catch (Exception e) { assetNames = "?" + Unwrap(e).Message; }

                sb.Append(" 载入OK name=").Append(ab.name).Append(" 资源数=").Append(assetNames);

                // 不能拿内部名当别名注册：这个包的 bundle 内部名是 chara/mt_ramp_00.unity3d
                // （作者拿模板改的没改 AB 名），注册成别名会把原版 mt_ramp 包顶掉，材质/贴图全乱。
                // 旧版本留下的这种别名要在本次重载里顺手摘掉（只摘指向同一个加载器列表的）。
                if (!string.IsNullOrEmpty(ab.name) && !SameBundleKey(ab.name, name))
                {
                    try
                    {
                        if (bundles.Contains(ab.name) && ListHasOurLoader(bundles[ab.name]))
                        {
                            bundles.Remove(ab.name);
                            sb.Append("（清掉旧别名 ").Append(ab.name).Append("）");
                        }
                        else
                        {
                            sb.Append("（内部名≠注册名，忽略）");
                        }
                    }
                    catch (Exception)
                    {
                        sb.Append("（内部名≠注册名，忽略）");
                    }
                }
            }

            return sb.ToString();
        }

        /// <summary>这个键下面挂的是不是我们自己注册的加载器（用于清理旧版本留下的别名）。</summary>
        static bool ListHasOurLoader(object listObj)
        {
            var list = listObj as IList;
            if (list == null) return false;

            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item == null) continue;

                var del = item as Delegate;
                if (del != null && del.Target is CachedBundleLoader) return true;

                // LazyCustom<AssetBundle> 里面包着 Func<AssetBundle>
                var fields = item.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int f = 0; f < fields.Length; f++)
                {
                    var inner = fields[f].GetValue(item) as Delegate;
                    if (inner != null && inner.Target is CachedBundleLoader) return true;
                }
            }
            return false;
        }

        /// <summary>把 LazyCustom&lt;AssetBundle&gt; 强制转成 AssetBundle（= 真正触发一次加载）。</summary>
        static string LoadLazyBundle(object lazy, out AssetBundle ab)
        {
            ab = null;
            if (lazy == null) return null;

            try
            {
                var t = lazy.GetType();
                var op = t.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { t }, null);
                if (op == null) return "没有 implicit 转换";
                ab = op.Invoke(null, new object[] { lazy }) as AssetBundle;
                return null;
            }
            catch (Exception e)
            {
                return Unwrap(e).GetType().Name + ": " + Unwrap(e).Message;
            }
        }

        /// <summary>
        /// 重读这个包里的 characustom 表格。
        /// 注意 Sideloader 的 Lists.LoadList 只往 dictListInfo 里"补缺"，同 ID 的行永远不会被覆盖，
        /// 所以光调 LoadList 会出现"日志说读了 5 张表，游戏里还是旧名字"。这里补齐三件事：
        ///   1) 复用上次分配到的 slot，避免每重载一次就多出一整套 ID
        ///   2) 先删掉这个包在 dictListInfo / ExternalDataList 里的旧条目，再按新数据加回去
        ///   3) 重新生成 AutoResolver 的 ResolveInfo（卡片里写的 guid 就是从这里来的）
        /// </summary>
        static string MergeLists(object zip, object chaListControl)
        {
            if (chaListControl == null) return "csv 跳过（没拿到 ChaListControl）";

            var manifest = LoadManifestFromZip(zip);
            var guid = ManifestString(manifest, "GUID");
            var manifestError = _manifestError;

            int ok = 0;
            int fail = 0;
            int added = 0;
            int updated = 0;
            int removed = 0;
            string firstError = null;
            var seenCategories = new HashSet<int>();
            var shortfall = new List<string>();

            foreach (var entry in EnumerateEntries(zip))
            {
                var entryName = GetEntryName(entry);
                if (entryName == null) continue;
                if (!entryName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
                if (entryName.IndexOf("list/characustom", StringComparison.OrdinalIgnoreCase) < 0) continue;

                try
                {
                    var stream = GetEntryStream(zip, entry);
                    if (stream == null) continue;

                    using (stream)
                    {
                        var data = _loadCsv.Invoke(null, new object[] { stream });
                        if (data == null) continue;

                        // 和启动时同一条路：Possess 改 1（DisplaySlotInName 时的名字前缀也在这里）
                        if (_setPossessNew != null)
                        {
                            try { _setPossessNew.Invoke(_instance, new object[] { data }); }
                            catch (Exception) { }
                        }

                        int a;
                        int u;
                        int total;
                        int landed;
                        var category = CategoryOf(data);
                        RefreshListData(chaListControl, data, manifest, guid, out a, out u, out total, out landed);
                        added += a;
                        updated += u;
                        ok++;
                        seenCategories.Add(category);

                        // 入列不全 = 数据换好了但没进 ChaListControl（多半是类目还没初始化）
                        if (total > 0 && landed < total) shortfall.Add("类目" + category + " 入列=" + landed + "/" + total);
                    }
                }
                catch (Exception e)
                {
                    // 单张表失败不影响其他表
                    fail++;
                    if (firstError == null) firstError = Unwrap(e).Message + " (" + entryName + ")";
                }
            }

            // 整个 csv 被删掉的情况：包里的类目少了，旧条目也得跟着消失，否则"删了还在"
            if (fail == 0 && ok > 0 && !string.IsNullOrEmpty(guid))
                removed = RemoveMissingCategories(chaListControl, guid, seenCategories);

            var sb = new System.Text.StringBuilder();
            sb.Append("csv 成功=").Append(ok).Append(" 失败=").Append(fail);
            sb.Append(" 换掉旧条目=").Append(updated).Append(" 新条目=").Append(added);
            if (removed > 0) sb.Append(" 删掉缺失类目条目=").Append(removed);
            if (shortfall.Count > 0) sb.Append(" 入列不全: ").Append(string.Join("，", shortfall.ToArray()));
            if (string.IsNullOrEmpty(guid))
                sb.Append("（没读到 manifest，未分配 slot")
                  .Append(string.IsNullOrEmpty(manifestError) ? "" : ": " + manifestError)
                  .Append("）");
            if (firstError != null) sb.Append(" 首个错误: ").Append(firstError);
            return sb.ToString();
        }

        /// <summary>zip 里已经没有这个类目的 csv 了：把这个包在该类目下的旧条目一并清掉。</summary>
        static int RemoveMissingCategories(object chaListControl, string guid, HashSet<int> keep)
        {
            try
            {
                if (_riGuid == null || _riLocalSlot == null || _riCategoryNo == null) return 0;

                var all = GetLoadedResolveInfos();
                var dropped = new Dictionary<int, HashSet<int>>();
                var remaining = NewResolveInfoList();
                if (remaining == null) return 0;

                for (int i = 0; i < all.Count; i++)
                {
                    var info = all[i];
                    var category = CategoryOfResolve(info);
                    if (StringProp(info, _riGuid) == guid && !keep.Contains(category))
                    {
                        HashSet<int> locals;
                        if (!dropped.TryGetValue(category, out locals))
                        {
                            locals = new HashSet<int>();
                            dropped[category] = locals;
                        }
                        locals.Add(IntProp(info, _riLocalSlot));
                        continue;
                    }

                    remaining.Add(info);
                }

                if (dropped.Count == 0) return 0;

                var dictInfo = _dictListInfoField == null ? null : _dictListInfoField.GetValue(chaListControl) as IDictionary;
                var ext = _externalDataListProp == null ? null : _externalDataListProp.GetValue(null, null) as IList;
                int removed = 0;

                foreach (var kv in dropped)
                {
                    var catDict = FindCategoryDict(dictInfo, kv.Key);
                    if (catDict != null)
                    {
                        foreach (var local in kv.Value)
                            if (catDict.Contains(local)) { catDict.Remove(local); removed++; }
                    }

                    if (ext == null) continue;

                    var drop = new List<int>();
                    for (int i = 0; i < ext.Count; i++)
                    {
                        var d = ext[i];
                        if (d == null || CategoryOf(d) != kv.Key) continue;
                        if (SharesLocalSlots(d, kv.Value)) drop.Add(i);
                    }
                    for (int i = drop.Count - 1; i >= 0; i--) ext.RemoveAt(drop[i]);
                }

                if (_setResolveInfos != null) _setResolveInfos.Invoke(null, new object[] { remaining });
                return removed;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// 用新解析出来的表格替换某个类目的旧数据。
        /// slot 复用：Sideloader 的 ResolveInfo 里 Slot=作者 CSV 里的 ID，LocalSlot=游戏内实际分配的 ID。
        /// </summary>
        static void RefreshListData(object chaListControl, object data, object manifest, string guid,
            out int added, out int updated, out int total, out int landed)
        {
            added = 0;
            updated = 0;
            total = 0;
            landed = 0;

            var category = CategoryOf(data);
            var rows = RowsOf(data);
            if (rows == null) return;

            var canResolve = !string.IsNullOrEmpty(guid)
                && _loadedResolveInfosProp != null && _riGuid != null && _riSlot != null && _riLocalSlot != null;

            var oldInfos = canResolve ? FindResolveInfos(guid, category) : new List<object>();

            var oldBySlot = new Dictionary<int, object>();
            var oldLocals = new HashSet<int>();
            for (int i = 0; i < oldInfos.Count; i++)
            {
                var info = oldInfos[i];
                var slot = IntProp(info, _riSlot);
                if (!oldBySlot.ContainsKey(slot)) oldBySlot[slot] = info;
                oldLocals.Add(IntProp(info, _riLocalSlot));
            }

            // 让 Sideloader 自己生成 ResolveInfo：属性分类、槽位分配全按它的规则来。
            // 它会顺手把每行第一列（ID）改成本次新分配的 LocalSlot。
            var fresh = !canResolve || _generateResolutionInfo == null || manifest == null ? null : NewResolveInfoList();
            if (fresh != null)
            {
                try { _generateResolutionInfo.Invoke(null, new object[] { manifest, data, fresh }); }
                catch (Exception) { fresh = null; }
            }

            if (fresh == null)
            {
                // 没有 AutoResolver 可用：只能按老办法"补缺"，改名的行刷不出来，但不会把 ID 弄乱
                try { _loadList.Invoke(null, new object[] { chaListControl, data }); }
                catch (Exception) { }
                return;
            }

            // 把作者原来的 ID 重新对回上次分配的 LocalSlot
            var remap = new Dictionary<int, int>();
            for (int i = 0; i < fresh.Count; i++)
            {
                var info = fresh[i];
                var local = IntProp(info, _riLocalSlot);

                object oldInfo;
                if (!oldBySlot.TryGetValue(IntProp(info, _riSlot), out oldInfo)) continue;

                var oldLocal = IntProp(oldInfo, _riLocalSlot);
                if (oldLocal == local) continue;

                SetIntProp(info, _riLocalSlot, oldLocal);
                if (!remap.ContainsKey(local)) remap[local] = oldLocal;
            }

            if (remap.Count > 0)
            {
                foreach (DictionaryEntry row in rows)
                {
                    var cells = row.Value as IList<string>;
                    if (cells == null || cells.Count == 0) continue;

                    int v;
                    if (int.TryParse(cells[0], out v) && remap.ContainsKey(v)) cells[0] = remap[v].ToString();
                }
            }

            var finalLocals = new HashSet<int>();
            for (int i = 0; i < fresh.Count; i++) finalLocals.Add(IntProp(fresh[i], _riLocalSlot));
            foreach (var local in finalLocals)
                if (!oldLocals.Contains(local)) added++;

            // 1) 删掉旧条目：LoadList 只会补缺，不删的话新名字永远进不去
            var dictInfo = _dictListInfoField == null ? null : _dictListInfoField.GetValue(chaListControl) as IDictionary;
            var catDict = FindCategoryDict(dictInfo, category);
            if (catDict != null)
            {
                foreach (var local in oldLocals)
                {
                    if (!catDict.Contains(local)) continue;
                    catDict.Remove(local);
                    updated++;
                }
            }

            // 2) 按新数据补回去
            try { _loadList.Invoke(null, new object[] { chaListControl, data }); }
            catch (Exception) { }

            // 落列自检：新数据里的条目是否真的进了 ChaListControl（类目没初始化时 LoadList 会空跑）
            catDict = FindCategoryDict(dictInfo, category);
            foreach (var local in finalLocals)
            {
                total++;
                if (catDict != null && catDict.Contains(local)) landed++;
            }

            // 3) Maker 重建列表读的是 ExternalDataList，旧对象不换掉就算 dictListInfo 更新了也还是旧名字
            ReplaceExternalList(data, category, oldLocals);

            // 4) 卡片里的 guid 来自 ResolveInfo 解析表，必须一起换
            RebuildResolveInfos(guid, category, fresh);
        }

        static void ReplaceExternalList(object data, int category, HashSet<int> oldLocals)
        {
            try
            {
                var ext = _externalDataListProp == null ? null : _externalDataListProp.GetValue(null, null) as IList;
                if (ext == null) return;

                var drop = new List<int>();
                if (oldLocals.Count > 0)
                {
                    for (int i = 0; i < ext.Count; i++)
                    {
                        var d = ext[i];
                        if (d == null || CategoryOf(d) != category) continue;
                        if (SharesLocalSlots(d, oldLocals)) drop.Add(i);
                    }
                }

                for (int i = drop.Count - 1; i >= 0; i--) ext.RemoveAt(drop[i]);
                ext.Add(data);
            }
            catch (Exception)
            {
            }
        }

        static void RebuildResolveInfos(string guid, int category, IList fresh)
        {
            if (_setResolveInfos == null || string.IsNullOrEmpty(guid) || fresh == null) return;

            try
            {
                var merged = NewResolveInfoList();
                if (merged == null) return;

                var all = GetLoadedResolveInfos();
                for (int i = 0; i < all.Count; i++)
                {
                    var info = all[i];
                    // 旧记录整条换掉（同一个 guid 的同一个类目）
                    if (StringProp(info, _riGuid) == guid && CategoryOfResolve(info) == category) continue;
                    merged.Add(info);
                }

                for (int i = 0; i < fresh.Count; i++) merged.Add(fresh[i]);

                _setResolveInfos.Invoke(null, new object[] { merged });
            }
            catch (Exception)
            {
            }
        }

        static List<object> FindResolveInfos(string guid, int category)
        {
            var result = new List<object>();
            var all = GetLoadedResolveInfos();
            for (int i = 0; i < all.Count; i++)
            {
                var info = all[i];
                if (StringProp(info, _riGuid) != guid) continue;
                if (CategoryOfResolve(info) != category) continue;
                result.Add(info);
            }
            return result;
        }

        static List<object> GetLoadedResolveInfos()
        {
            var result = new List<object>();
            try
            {
                if (_loadedResolveInfosProp == null) return result;

                var e = _loadedResolveInfosProp.GetValue(null, null) as IEnumerable;
                if (e == null) return result;

                foreach (var info in e)
                    if (info != null) result.Add(info);
            }
            catch (Exception)
            {
            }
            return result;
        }

        static IList NewResolveInfoList()
        {
            try
            {
                return _resolveInfoListType == null ? null : (IList)Activator.CreateInstance(_resolveInfoListType);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static IDictionary FindCategoryDict(IDictionary dictInfo, int category)
        {
            if (dictInfo == null) return null;

            try
            {
                foreach (DictionaryEntry e in dictInfo)
                    if (Convert.ToInt32(e.Key) == category) return e.Value as IDictionary;
            }
            catch (Exception)
            {
            }
            return null;
        }

        static bool SharesLocalSlots(object data, HashSet<int> locals)
        {
            var rows = RowsOf(data);
            if (rows == null) return false;

            foreach (DictionaryEntry row in rows)
            {
                var cells = row.Value as IList<string>;
                if (cells == null || cells.Count == 0) continue;

                int v;
                if (int.TryParse(cells[0], out v) && locals.Contains(v)) return true;
            }
            return false;
        }

        static int CategoryOf(object chaListData)
        {
            try
            {
                var p = chaListData.GetType().GetProperty("categoryNo");
                return p == null ? -1 : Convert.ToInt32(p.GetValue(chaListData, null));
            }
            catch (Exception)
            {
                return -1;
            }
        }

        static int CategoryOfResolve(object info)
        {
            try
            {
                return _riCategoryNo == null ? -1 : Convert.ToInt32(_riCategoryNo.GetValue(info, null));
            }
            catch (Exception)
            {
                return -1;
            }
        }

        static IDictionary RowsOf(object chaListData)
        {
            try
            {
                var p = chaListData.GetType().GetProperty("dictList");
                return p == null ? null : p.GetValue(chaListData, null) as IDictionary;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static int IntProp(object info, PropertyInfo property)
        {
            try
            {
                return property == null ? 0 : Convert.ToInt32(property.GetValue(info, null));
            }
            catch (Exception)
            {
                return 0;
            }
        }

        static void SetIntProp(object info, PropertyInfo property, int value)
        {
            try
            {
                if (property == null || !property.CanWrite) return;
                property.SetValue(info, Convert.ChangeType(value, property.PropertyType), null);
            }
            catch (Exception)
            {
            }
        }

        static string StringProp(object info, PropertyInfo property)
        {
            try
            {
                return property == null ? null : property.GetValue(info, null) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>读条目字节、修 CAB、从内存加载。返回 null 表示失败。</summary>
        internal static AssetBundle LoadBundleFromZip(object zip, object entry)
        {
            try
            {
                var bytes = ReadEntryBytes(zip, entry);
                if (bytes == null || bytes.Length == 0) return null;

                // 同一 CAB 名多次 LoadFromMemory 会被 Unity 去重，必须随机化
                if (_randomizeCab != null)
                {
                    try { _randomizeCab.Invoke(null, new object[] { bytes }); }
                    catch (Exception) { }
                }

                return AssetBundle.LoadFromMemory(bytes);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- bundle 磁盘缓存

        /// <summary>缓存目录：BepInEx/hotreload/bundle-cache，按内容哈希命名。</summary>
        static string CacheDir
        {
            get
            {
                try { return Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "hotreload"), "bundle-cache"); }
                catch (Exception) { return Path.Combine(Path.GetTempPath(), "hotreload-bundle-cache"); }
            }
        }

        static string Sha1Hex(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var hash = sha.ComputeHash(data);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// 把 zip 里的 unity3d 抽到磁盘缓存，按内容哈希命名（同内容只存一份）。
        /// Unity 只锁缓存文件，源 zipmod 随时可以覆盖。
        /// </summary>
        static string ExtractToCache(object zip, object entry)
        {
            var bytes = ReadEntryBytes(zip, entry);
            if (bytes == null || bytes.Length < 16) throw new InvalidOperationException("读不出条目内容");

            if (!(bytes[0] == 'U' && bytes[1] == 'n' && bytes[2] == 'i'))
                throw new InvalidOperationException("不是 Unity AssetBundle");

            var path = Path.Combine(CacheDir, Sha1Hex(bytes) + ".unity3d");
            if (File.Exists(path) && new FileInfo(path).Length == bytes.Length)
            {
                TouchCacheFile(path);
                return path;
            }

            Directory.CreateDirectory(CacheDir);

            // CAB 保险：反复替换时旧 bundle 可能还没卸干净，随机化 CAB 让新旧不撞名
            if (_randomizeCab != null)
            {
                try { _randomizeCab.Invoke(null, new object[] { bytes }); } catch (Exception) { }
            }

            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        // 缓存上限：总量超过 1GB，或超过 15 天没再被用到，就清掉
        const long CacheMaxBytes = 1L * 1024 * 1024 * 1024;
        const int CacheMaxAgeDays = 15;

        static void TouchCacheFile(string path)
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (Exception) { }
        }

        /// <summary>
        /// 清理磁盘缓存：先删过期的，再按总量上限从最旧的删。
        /// 正在被 Unity 加载的文件删不掉（文件锁），跳过留到下次。
        /// </summary>
        public static string PruneCache()
        {
            try
            {
                var dir = CacheDir;
                if (!Directory.Exists(dir)) return null;

                var paths = Directory.GetFiles(dir, "*.unity3d");
                if (paths.Length == 0) return null;

                var files = new List<FileInfo>();
                long total = 0;
                for (int i = 0; i < paths.Length; i++)
                {
                    var fi = new FileInfo(paths[i]);
                    total += fi.Length;
                    files.Add(fi);
                }

                files.Sort(delegate (FileInfo a, FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });

                var cutoff = DateTime.UtcNow.AddDays(-CacheMaxAgeDays);
                int removed = 0;
                long freed = 0;

                for (int i = 0; i < files.Count; i++)
                {
                    var fi = files[i];
                    if (fi.LastWriteTimeUtc >= cutoff && total <= CacheMaxBytes) continue;

                    try
                    {
                        long len = fi.Length;
                        fi.Delete();
                        total -= len;
                        freed += len;
                        removed++;
                    }
                    catch (Exception)
                    {
                        // 正在被 Unity 占用，下次再说
                    }
                }

                if (removed == 0)
                    return "bundle 缓存 " + (total / 1048576) + "MB（上限 " + (CacheMaxBytes / 1048576) + "MB）";

                return "bundle 缓存清理：删除 " + removed + " 个，释放 " + (freed / 1048576) + "MB，当前 "
                    + (total / 1048576) + "MB（上限 " + (CacheMaxBytes / 1048576) + "MB）";
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>把"从缓存文件加载"的委托注册进 BundleManager（不同版本参数个数不同）。</summary>
        static void RegisterCachedLoader(string bundleName, string cachePath)
        {
            if (_addBundleLoader == null) throw new InvalidOperationException("AddBundleLoader 缺失");

            var loader = new CachedBundleLoader(cachePath);
            var del = (Func<AssetBundle>)Delegate.CreateDelegate(
                typeof(Func<AssetBundle>), loader,
                typeof(CachedBundleLoader).GetMethod("Load"));

            var ps = _addBundleLoader.GetParameters();
            if (ps.Length >= 3) _addBundleLoader.Invoke(null, new object[] { del, bundleName, null });
            else _addBundleLoader.Invoke(null, new object[] { del, bundleName });
        }

        sealed class CachedBundleLoader
        {
            readonly string _path;
            public CachedBundleLoader(string path) { _path = path; }

            public AssetBundle Load()
            {
                try { return AssetBundle.LoadFromFile(_path); }
                catch (Exception) { return null; }
            }
        }

        // ---------------------------------------------------------------- 小工具

        static byte[] ReadEntryBytes(object zip, object entry)
        {
            var stream = GetEntryStream(zip, entry);
            if (stream == null) return null;

            using (stream)
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    ms.Write(buffer, 0, read);
                return ms.ToArray();
            }
        }

        static Stream GetEntryStream(object zip, object entry)
        {
            var mi = zip.GetType().GetMethod("GetInputStream", new[] { entry.GetType() });
            if (mi == null) return null;
            return mi.Invoke(zip, new[] { entry }) as Stream;
        }

        static IEnumerable<object> EnumerateEntries(object zip)
        {
            var enumerable = zip as IEnumerable;
            if (enumerable == null) yield break;
            foreach (var e in enumerable) yield return e;
        }

        static string GetEntryName(object entry)
        {
            var p = entry.GetType().GetProperty("Name");
            return p == null ? null : p.GetValue(entry, null) as string;
        }

        static string NormalizeBundleName(string entryName)
        {
            var name = entryName.Replace('\\', '/');
            if (name.StartsWith("abdata/", StringComparison.OrdinalIgnoreCase))
                name = name.Substring("abdata/".Length);
            return name;
        }

        static string GetZipName(object zip)
        {
            var p = zip.GetType().GetProperty("Name");
            if (p != null)
            {
                var v = p.GetValue(zip, null) as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return zip.ToString();
        }

        static void InvokeIfExists(object target, string method)
        {
            var mi = target.GetType().GetMethod(method, Type.EmptyTypes);
            if (mi == null) return;
            try { mi.Invoke(target, null); } catch (Exception) { }
        }

        static bool PathEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        sealed class ZipBundleLoader
        {
            readonly object _zip;
            readonly object _entry;

            public ZipBundleLoader(object zip, object entry)
            {
                _zip = zip;
                _entry = entry;
            }

            public AssetBundle Load()
            {
                return LoadBundleFromZip(_zip, _entry);
            }
        }
    }
}
