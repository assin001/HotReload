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
    /// RX22 / HF Patch 系 Sideloader 桥接。
    /// 这一版 Sideloader 的内部结构和 Madevil 那套完全不同，关键点：
    ///   Sideloader.Sideloader.Zipmods        Dictionary&lt;guid, ZipmodInfo&gt;（静态私有）
    ///   ZipmodInfo.GetZipFile()             懒加载 new ZipFile(FileName)
    ///   ZipmodInfo.Dispose()                关闭 zip，文件随即解锁
    ///   ZipmodInfo.LoadAllLists()           解析 manifest + 填充 BundleInfos / CharaLists / PngNames
    ///   Sideloader.Sideloader.AddBundles(ICollection&lt;BundleLoadInfo&gt;)  注册 bundle 加载器
    ///   Sideloader.BundleManager.AddBundleLoader(Func&lt;AssetBundle&gt;, string)  只有两个参数
    ///   BundleLoadInfo.BundleTrimmedPath    如 "chara/xxx.unity3d"
    /// 全部走反射，避免对 Sideloader.dll 的编译期依赖（它各版本结构差异很大）。
    /// </summary>
    internal static class SideloaderBridge
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        static Type _sideloaderType;
        static object _instance;

        static FieldInfo _zipmodsField;
        static FieldInfo _pngListField;
        static FieldInfo _manifestsField;
        static FieldInfo _zipArchivesField;

        static Type _zipmodType;
        static ConstructorInfo _zipmodCtor;
        static MethodInfo _zipmodDispose;
        static MethodInfo _zipmodGetZipFile;
        static MethodInfo _zipmodLoadAllLists;
        static FieldInfo _zipmodManifestField;
        static FieldInfo _zipmodBundleInfosField;
        static FieldInfo _zipmodCharaListsField;
        static FieldInfo _zipmodFileNameField;
        static FieldInfo _zipmodLoadedField;

        static Type _manifestType;
        static MethodInfo _manifestLoadFromZip;

        static Type _bundleInfoType;
        static PropertyInfo _bundleTrimmedPath;

        static Type _bundleManagerType;
        static FieldInfo _bundlesField;

        static MethodInfo _addBundles;
        static MethodInfo _addBundleLoader;
        static MethodInfo _randomizeCab;
        static MethodInfo _buildPngFolderList;
        static MethodInfo _loadList;

        // ---- 表格/manifest 真正刷新要用的入口（Sideloader 自己的列表与 AutoResolver 解析表）----
        static FieldInfo _loadedManifestsField;
        static FieldInfo _dictListInfoField;          // ChaListControl.dictListInfo
        static PropertyInfo _externalDataListProp;    // Lists.ExternalDataList
        static Type _resolveInfoType;                 // Sideloader.AutoResolver.ResolveInfo
        static Type _resolveInfoListType;
        static PropertyInfo _riGuid, _riSlot, _riLocalSlot, _riCategoryNo;
        static PropertyInfo _loadedResolveInfosProp;  // UniversalAutoResolver.LoadedResolutionInfo
        static MethodInfo _generateResolutionInfo;
        static MethodInfo _setResolveInfos;

        static Type _abmType;
        static MethodInfo _unloadAssetBundle;
        static FieldInfo _mainManifestField;

        public static bool Available { get; private set; }
        public static string LastError { get; private set; }

        /// <summary>启动时探测到的接口可用情况（写进日志用）。</summary>
        public static string Capabilities { get; private set; }

        public static void Initialize()
        {
            Available = false;
            LastError = null;

            _sideloaderType = FindType("Sideloader.Sideloader");
            if (_sideloaderType == null) { LastError = "找不到 Sideloader.Sideloader"; return; }

            _zipmodsField = _sideloaderType.GetField("Zipmods", AnyStatic);
            if (_zipmodsField == null)
            {
                LastError = "Sideloader.Zipmods 不存在（这是旧版 Sideloader，请用 Madevil 版 payload）";
                return;
            }

            _pngListField = _sideloaderType.GetField("PngList", AnyStatic);
            _manifestsField = _sideloaderType.GetField("Manifests", AnyStatic);
            _zipArchivesField = _sideloaderType.GetField("ZipArchives", AnyStatic);

            foreach (var kv in Chainloader.PluginInfos)
            {
                var info = kv.Value;
                if (info == null || info.Instance == null) continue;
                if (info.Instance.GetType() == _sideloaderType) { _instance = info.Instance; break; }
            }
            if (_instance == null) { LastError = "Sideloader 插件实例未找到（未安装？）"; return; }

            var zipmodsType = _zipmodsField.FieldType;
            if (!zipmodsType.IsGenericType) { LastError = "Zipmods 不是 Dictionary"; return; }
            _zipmodType = zipmodsType.GetGenericArguments()[1];

            _zipmodCtor = _zipmodType.GetConstructor(Any, null, new[] { typeof(string) }, null);
            _zipmodDispose = _zipmodType.GetMethod("Dispose", Any, null, Type.EmptyTypes, null);
            _zipmodGetZipFile = _zipmodType.GetMethod("GetZipFile", Any, null, Type.EmptyTypes, null);
            _zipmodLoadAllLists = _zipmodType.GetMethod("LoadAllLists", Any, null, Type.EmptyTypes, null);
            _zipmodManifestField = _zipmodType.GetField("Manifest", Any);
            _zipmodBundleInfosField = _zipmodType.GetField("BundleInfos", Any);
            _zipmodCharaListsField = _zipmodType.GetField("CharaLists", Any);
            _zipmodFileNameField = _zipmodType.GetField("FileName", Any);
            _zipmodLoadedField = _zipmodType.GetField("Loaded", Any);

            if (_zipmodCtor == null) { LastError = "ZipmodInfo(string) 构造器没了"; return; }
            if (_zipmodGetZipFile == null) { LastError = "ZipmodInfo.GetZipFile 没了"; return; }
            if (_zipmodLoadAllLists == null) { LastError = "ZipmodInfo.LoadAllLists 没了"; return; }
            if (_zipmodBundleInfosField == null) { LastError = "ZipmodInfo.BundleInfos 没了"; return; }

            _bundleInfoType = _zipmodBundleInfosField.FieldType;
            if (_bundleInfoType.IsGenericType) _bundleInfoType = _bundleInfoType.GetGenericArguments()[0];
            _bundleTrimmedPath = _bundleInfoType.GetProperty("BundleTrimmedPath", Any);

            _bundleManagerType = FindType("Sideloader.BundleManager");
            if (_bundleManagerType != null)
            {
                _bundlesField = _bundleManagerType.GetField("Bundles", AnyStatic);
                _addBundleLoader = _bundleManagerType.GetMethod("AddBundleLoader", AnyStatic, null,
                    new[] { typeof(Func<AssetBundle>), typeof(string) }, null);
                _randomizeCab = _bundleManagerType.GetMethod("RandomizeCAB", AnyStatic, null,
                    new[] { typeof(byte[]) }, null);
            }

            _addBundles = FindMethod(_sideloaderType, "AddBundles", 1);
            _buildPngFolderList = FindMethod(_sideloaderType, "BuildPngFolderList", 1);

            _manifestType = FindType("Sideloader.Manifest");
            if (_manifestType != null)
            {
                var zipFileType = _zipmodGetZipFile.ReturnType;
                _manifestLoadFromZip = _manifestType.GetMethod("LoadFromZip", AnyStatic, null,
                    new[] { zipFileType }, null);
            }

            var listsType = FindType("Sideloader.ListLoader.Lists");
            var chaListControlType = FindType("ChaListControl");
            var chaListDataType = FindType("ChaListData");
            if (listsType != null && chaListControlType != null && chaListDataType != null)
                _loadList = listsType.GetMethod("LoadList", AnyStatic, null,
                    new[] { chaListControlType, chaListDataType }, null);

            // ---- 表格 / manifest 刷新所需的额外入口 ----
            _loadedManifestsField = _sideloaderType.GetField("LoadedManifests", AnyStatic);
            if (chaListControlType != null)
                _dictListInfoField = chaListControlType.GetField("dictListInfo", Any);
            if (listsType != null)
                _externalDataListProp = listsType.GetProperty("ExternalDataList", AnyStatic);

            var resolverType = FindType("Sideloader.AutoResolver.UniversalAutoResolver");
            _resolveInfoType = FindType("Sideloader.AutoResolver.ResolveInfo");
            if (resolverType != null && _resolveInfoType != null)
            {
                _resolveInfoListType = typeof(List<>).MakeGenericType(_resolveInfoType);
                _riGuid = _resolveInfoType.GetProperty("GUID", Any);
                _riSlot = _resolveInfoType.GetProperty("Slot", Any);
                _riLocalSlot = _resolveInfoType.GetProperty("LocalSlot", Any);
                _riCategoryNo = _resolveInfoType.GetProperty("CategoryNo", Any);
                _loadedResolveInfosProp = resolverType.GetProperty("LoadedResolutionInfo", AnyStatic);
                _generateResolutionInfo = resolverType.GetMethod("GenerateResolutionInfo", AnyStatic);
                _setResolveInfos = resolverType.GetMethod("SetResolveInfos", AnyStatic);
            }

            // AssetBundleManager 缓存（这一版和 Madevil 那套一致）
            _abmType = FindType("AssetBundleManager");
            if (_abmType != null)
            {
                _unloadAssetBundle = _abmType.GetMethod("UnloadAssetBundle",
                    new[] { typeof(string), typeof(bool), typeof(string), typeof(bool) });
                _mainManifestField = _abmType.GetField("MAIN_MANIFEST_NAME", AnyStatic);
            }

            // 启动时就把关键接口是否找到写进日志，省得出问题只能看到 NRE
            Capabilities = "AddBundles=" + (_addBundles == null ? "缺" : "OK")
                + " AddBundleLoader=" + (_addBundleLoader == null ? "缺" : "OK")
                + " RandomizeCAB=" + (_randomizeCab == null ? "缺" : "OK")
                + " PngList=" + (_buildPngFolderList == null ? "缺" : "OK")
                + " ABM=" + (_abmType == null ? "缺" : "OK")
                + " Manifest=" + (_manifestLoadFromZip == null ? "缺" : "OK")
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

        static MethodInfo FindMethod(Type t, string name, int paramCount)
        {
            // 必须带 Static：HF/RX22 这版 Sideloader 的 AddBundles、BuildPngFolderList 都是 private static，
            // 只查实例方法会返回 null，后面 Invoke 直接 NRE。
            var ms = t.GetMethods(Any | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                if (ms[i].Name == name && ms[i].GetParameters().Length == paramCount) return ms[i];
            }
            return null;
        }

        /// <summary>静态方法传 null，实例方法传实例，避免 "Object does not match target type"。</summary>
        static object TargetOf(MethodInfo mi)
        {
            return mi != null && mi.IsStatic ? null : _instance;
        }

        // ---------------------------------------------------------------- 句柄释放

        /// <summary>把 zipmod 从 Sideloader 的注册表里摘掉并关闭 zip，文件随即解锁。</summary>
        public static bool ReleaseArchive(string zipPath, out string detail)
        {
            detail = null;
            if (!Available) { detail = "桥接不可用: " + LastError; return false; }

            int closed = RemoveZipmods(_zipmodsField, zipPath);
            closed += RemoveZipmods(_pngListField, zipPath);

            var zipmods = _zipmodsField.GetValue(null) as IDictionary;
            detail = "已注册=" + (zipmods == null ? -1 : zipmods.Count) + " 关闭=" + closed;
            return closed > 0;
        }

        static int RemoveZipmods(FieldInfo field, string zipPath)
        {
            if (field == null) return 0;

            var map = field.GetValue(null) as IDictionary;
            if (map == null) return 0;

            var keys = new List<object>();
            foreach (DictionaryEntry e in map)
            {
                if (SameArchive(GetZipmodFileName(e.Value), zipPath)) keys.Add(e.Key);
            }

            for (int i = 0; i < keys.Count; i++)
            {
                var info = map[keys[i]];
                map.Remove(keys[i]);
                // 先卸掉这个包已经加载的 AssetBundle，再关 zip。
                // Unity 用 LoadFromFile(archive, 0, offset) 流式读包时，只要 bundle 还在，
                // 它就一直攥着 zip 的文件句柄——光 Close 掉 ZipFile 是解不了锁的。
                DetachZipmodBundles(info);
                DisposeZipmod(info);
            }

            return keys.Count;
        }

        /// <summary>把某个 zipmod 注册过的 bundle 从 BundleManager 里摘掉并卸载，让文件句柄释放。</summary>
        static void DetachZipmodBundles(object info)
        {
            if (info == null || _zipmodBundleInfosField == null || _bundlesField == null) return;

            IList bundleInfos = null;
            try { bundleInfos = _zipmodBundleInfosField.GetValue(info) as IList; }
            catch (Exception) { }
            if (bundleInfos == null) return;

            var bundles = _bundlesField.GetValue(null) as IDictionary;

            for (int i = 0; i < bundleInfos.Count; i++)
            {
                var name = GetBundleName(bundleInfos[i]);
                if (string.IsNullOrEmpty(name)) continue;

                try { HardUnloadBundle(name); } catch (Exception) { }
                if (bundles != null)
                {
                    try { bundles.Remove(name); } catch (Exception) { }
                }
            }
        }

        static string GetZipmodFileName(object info)
        {
            if (info == null || _zipmodFileNameField == null) return null;
            return _zipmodFileNameField.GetValue(info) as string;
        }

        static void DisposeZipmod(object info)
        {
            if (info == null || _zipmodDispose == null) return;
            try { _zipmodDispose.Invoke(info, null); } catch (Exception) { }
        }

        // ---------------------------------------------------------------- 重新装载

        /// <summary>重新打开 zip、重建 BundleInfos 并注册加载器，返回简报（不抛异常）。</summary>
        public static string ReloadArchive(string zipPath, object chaListControl, bool reloadLists)
        {
            if (!Available) return "失败[桥接不可用]: " + LastError;
            if (!File.Exists(zipPath)) return "失败[文件不存在]";

            object info = null;
            var steps = new System.Text.StringBuilder();

            try
            {
                // 1) 先解析新包，不动旧的：解析/校验失败就整段回退，旧状态原样保留（kept previous state）

                // 2) 新建 ZipmodInfo（FileName 由构造器填好）
                info = _zipmodCtor.Invoke(new object[] { zipPath });

                // 3) 打开 zip + 解析 manifest
                var zip = _zipmodGetZipFile.Invoke(info, null);
                if (zip == null)
                {
                    DisposeZipmod(info);
                    info = null;
                    return "失败[打开 zip]: GetZipFile 返回 null";
                }

                if (_manifestLoadFromZip != null && _zipmodManifestField != null)
                {
                    object manifest = null;
                    try { manifest = _manifestLoadFromZip.Invoke(null, new object[] { zip }); }
                    catch (Exception e)
                    {
                        DisposeZipmod(info);
                        info = null;
                        return "失败[解析 manifest]: " + Unwrap(e).Message;
                    }

                    if (manifest == null)
                    {
                        DisposeZipmod(info);
                        info = null;
                        return "失败[manifest 为空]: 不是有效 zipmod？";
                    }
                    _zipmodManifestField.SetValue(info, manifest);
                }

                // 4) 读 bundle 清单与表格
                _zipmodLoadAllLists.Invoke(info, null);

                var bundleInfos = _zipmodBundleInfosField.GetValue(info) as IList;
                if (bundleInfos == null)
                {
                    DisposeZipmod(info);
                    info = null;
                    return "失败[BundleInfos 为空]";
                }
                steps.Append("bundle 共" + bundleInfos.Count + " ");

                // 4.2) 校验 + 抽到磁盘缓存（内容寻址）。
                //      Unity 之后只锁缓存文件，源 zip 不再被 Unity 打开，
                //      所以覆盖前只需要让 Sideloader 关掉自己的句柄。
                var cached = new List<KeyValuePair<string, string>>();
                for (int i = 0; i < bundleInfos.Count; i++)
                {
                    var bundleName = GetBundleName(bundleInfos[i]);
                    if (string.IsNullOrEmpty(bundleName)) continue;

                    var entry = FindEntry(zip, bundleName);
                    if (entry == null)
                    {
                        DisposeZipmod(info);
                        info = null;
                        return "失败[" + steps + "zip 里找不到 " + bundleName + "]，kept previous state";
                    }

                    string cachePath;
                    try { cachePath = ExtractToCache(zip, entry); }
                    catch (Exception e)
                    {
                        DisposeZipmod(info);
                        info = null;
                        return "失败[" + steps + "缓存 " + bundleName + ": " + Unwrap(e).Message + "]，kept previous state";
                    }
                    cached.Add(new KeyValuePair<string, string>(bundleName, cachePath));
                }
                steps.Append("缓存=" + cached.Count + " ");

                // 4.5) 提交：到这里新包都验证通过了，才动旧的注册表
                var oldClosed = RemoveZipmods(_zipmodsField, zipPath) + RemoveZipmods(_pngListField, zipPath);
                steps.Append("清旧句柄=" + oldClosed + " ");

                //      Zipmods 的键是 FileName（BundleLoadInfo.LoadBundle 内部就是 Zipmods[ArchiveFilename]），
                //      只按 guid 注册会 KeyNotFoundException
                var fileName = _zipmodFileNameField == null ? null : _zipmodFileNameField.GetValue(info) as string;
                if (string.IsNullOrEmpty(fileName)) fileName = zipPath;

                var zipmods = _zipmodsField.GetValue(null) as IDictionary;
                if (zipmods == null)
                {
                    DisposeZipmod(info);
                    info = null;
                    return "失败[" + steps + "Zipmods 读取失败]";
                }
                zipmods[fileName] = info;

                var zipmodManifest = _zipmodManifestField == null ? null : _zipmodManifestField.GetValue(info);
                var zipmodGuid = GetGuid(zipmodManifest);
                var manifests = _manifestsField == null ? null : _manifestsField.GetValue(null) as IDictionary;
                if (manifests != null && !string.IsNullOrEmpty(zipmodGuid)) manifests[zipmodGuid] = zipmodManifest;

                if (_zipmodLoadedField != null)
                {
                    try { _zipmodLoadedField.SetValue(info, true); } catch (Exception) { }
                }

                // 5) 卸掉旧缓存 + 摘掉旧加载器
                var bundles = _bundlesField == null ? null : _bundlesField.GetValue(null) as IDictionary;
                int unloaded = 0;
                string unloadError = "";

                for (int i = 0; i < bundleInfos.Count; i++)
                {
                    var name = GetBundleName(bundleInfos[i]);
                    if (string.IsNullOrEmpty(name)) continue;

                    try { if (HardUnloadBundle(name)) unloaded++; }
                    catch (Exception e) { if (unloadError.Length == 0) unloadError = Unwrap(e).Message; }

                    if (bundles != null)
                    {
                        try { bundles.Remove(name); } catch (Exception) { }
                    }
                }
                steps.Append("卸载旧缓存=" + unloaded + " ");

                // 6) 注册缓存加载器（不再走 Sideloader 内部的 Zipmods 查表）
                bool registered = false;

                if (_addBundleLoader != null && cached.Count > 0)
                {
                    int ok = 0;
                    string regError = null;

                    for (int i = 0; i < cached.Count; i++)
                    {
                        try
                        {
                            RegisterCachedLoader(cached[i].Key, cached[i].Value);
                            ok++;
                        }
                        catch (Exception e)
                        {
                            if (regError == null) regError = Unwrap(e).Message;
                        }
                    }

                    steps.Append("注册=缓存" + ok + "/" + cached.Count + " ");
                    if (regError != null) steps.Append("首个注册错误: " + regError + " ");
                    registered = ok > 0;
                }

                if (!registered)
                {
                    if (_addBundles == null)
                    {
                        DisposeZipmod(info);
                        info = null;
                        return "失败[" + steps + "]: 找不到 AddBundleLoader/AddBundles（版本不兼容），kept previous state";
                    }
                    _addBundles.Invoke(TargetOf(_addBundles), new object[] { bundleInfos });
                    steps.Append("注册=AddBundles ");
                }

                // 7) 表格（默认跳过；manifest/表格基本不变）
                if (reloadLists) steps.Append(ApplyLists(info, chaListControl));

                // 8) 注册表已在 4.5 处理完（Zipmods 的键是 FileName，不是 guid）

                // 9) PNG 预览目录（失败无所谓）
                if (_buildPngFolderList != null)
                {
                    try { _buildPngFolderList.Invoke(TargetOf(_buildPngFolderList), new object[] { info }); } catch (Exception) { }
                }

                if (unloadError.Length > 0) steps.Append("卸载异常: " + unloadError + " ");
                return steps.ToString().Trim();
            }
            catch (Exception e)
            {
                if (info != null)
                {
                    // 注册失败的半成品要从 Zipmods 里摘掉，否则 Sideloader 会拿它去加载（然后 KeyNotFound）
                    try
                    {
                        var zipmods = _zipmodsField.GetValue(null) as IDictionary;
                        if (zipmods != null)
                        {
                            var staleKeys = new List<object>();
                            foreach (DictionaryEntry kv in zipmods)
                                if (ReferenceEquals(kv.Value, info)) staleKeys.Add(kv.Key);
                            for (int i = 0; i < staleKeys.Count; i++) zipmods.Remove(staleKeys[i]);
                        }
                    }
                    catch (Exception) { }

                    DisposeZipmod(info);
                }
                return "失败[" + steps + "]: " + Unwrap(e);
            }
        }

        static string GetBundleName(object bundleInfo)
        {
            if (bundleInfo == null || _bundleTrimmedPath == null) return null;
            try { return _bundleTrimmedPath.GetValue(bundleInfo, null) as string; }
            catch (Exception) { return null; }
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

        static IEnumerable<object> EnumerateEntries(object zip)
        {
            var enumerable = zip as IEnumerable;
            if (enumerable == null) yield break;
            foreach (var e in enumerable)
                if (e != null) yield return e;
        }

        static string GetEntryFullName(object entry)
        {
            var p = entry.GetType().GetProperty("Name");
            return p == null ? null : p.GetValue(entry, null) as string;
        }

        static string NormalizeEntryName(string name)
        {
            if (name == null) return null;
            name = name.Replace('\\', '/');
            if (name.StartsWith("abdata/", StringComparison.OrdinalIgnoreCase)) name = name.Substring(7);
            return name.TrimStart('/');
        }

        /// <summary>按 BundleTrimmedPath 在 zip 里找条目（忽略 abdata/ 前缀与大小写）。</summary>
        static object FindEntry(object zip, string bundleName)
        {
            var wanted = NormalizeEntryName(bundleName);
            if (string.IsNullOrEmpty(wanted)) return null;

            object byFileName = null;

            foreach (var entry in EnumerateEntries(zip))
            {
                var name = GetEntryFullName(entry);
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)) continue;

                if (string.Equals(NormalizeEntryName(name), wanted, StringComparison.OrdinalIgnoreCase))
                    return entry;

                if (byFileName == null &&
                    string.Equals(Path.GetFileName(name), Path.GetFileName(wanted), StringComparison.OrdinalIgnoreCase))
                    byFileName = entry;
            }

            return byFileName;
        }

        static byte[] ReadEntryBytes(object zip, object entry)
        {
            var getStream = zip.GetType().GetMethod("GetInputStream", new[] { entry.GetType() });
            if (getStream == null) return null;

            var s = getStream.Invoke(zip, new[] { entry }) as Stream;
            if (s == null) return null;

            using (s)
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[0x10000];
                int read;
                while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
                    ms.Write(buffer, 0, read);
                return ms.ToArray();
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
        /// 把 zip 内的 unity3d 抽到磁盘缓存，按内容哈希命名（同样内容只存一份）。
        /// Unity 之后加载的是缓存文件，源 zipmod 可以随时覆盖。
        /// </summary>
        static string ExtractToCache(object zip, object entry)
        {
            var bytes = ReadEntryBytes(zip, entry);
            if (bytes == null || bytes.Length < 16) throw new InvalidOperationException("读不出条目内容");

            // UnityFS / UnityWeb / UnityRaw
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

        /// <summary>把"从缓存文件加载"的委托注册进 BundleManager。</summary>
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

        static string GetGuid(object manifest)
        {
            if (manifest == null || _manifestType == null) return null;
            var p = _manifestType.GetProperty("GUID", Any);
            return p == null ? null : p.GetValue(manifest, null) as string;
        }

        /// <summary>
        /// 把新解析出来的 CharaLists 换进游戏列表。
        /// Sideloader 启动时（AddAllLists）做了三件事，热重载必须自己补齐，否则"日志说读了表，游戏里还是旧的"：
        ///   1) GenerateResolutionInfo —— 卡片里写的 guid 就来自这张解析表
        ///   2) Lists.ExternalDataList.Add —— Maker 重建列表读的是它
        ///   3) Lists.LoadList —— 只往 dictListInfo 里补缺，同 ID 的行永远不会被覆盖
        /// 另外必须复用上次分配的 LocalSlot，否则每重载一次就多出一整套 ID。
        /// </summary>
        static string ApplyLists(object info, object chaListControl)
        {
            if (chaListControl == null) return " csv 跳过（没拿到 ChaListControl）";
            if (_zipmodCharaListsField == null || _loadList == null) return " csv 跳过（接口缺失）";

            var lists = _zipmodCharaListsField.GetValue(info) as IList;
            if (lists == null || lists.Count == 0) return " csv 0";

            var manifest = _zipmodManifestField == null ? null : _zipmodManifestField.GetValue(info);
            var guid = GetGuid(manifest);

            int ok = 0;
            int fail = 0;
            int added = 0;
            int updated = 0;
            int removed = 0;
            string firstError = null;
            var seenCategories = new HashSet<int>();
            var shortfall = new List<string>();

            for (int i = 0; i < lists.Count; i++)
            {
                var data = lists[i];
                if (data == null) continue;

                try
                {
                    var category = CategoryOf(data);
                    int a;
                    int u;
                    int total;
                    int landed;
                    RefreshListData(chaListControl, data, manifest, guid, out a, out u, out total, out landed);
                    added += a;
                    updated += u;
                    ok++;
                    seenCategories.Add(category);

                    // 入列不全 = 数据换好了但没进 ChaListControl（多半是类目还没初始化）
                    if (total > 0 && landed < total) shortfall.Add("类目" + category + " 入列=" + landed + "/" + total);
                }
                catch (Exception e)
                {
                    fail++;
                    if (firstError == null) firstError = Unwrap(e).Message;
                }
            }

            // 整个 csv 被删掉的情况：包里的类目少了，旧条目也得跟着消失，否则"删了还在"
            if (fail == 0 && ok > 0 && !string.IsNullOrEmpty(guid))
                removed = RemoveMissingCategories(chaListControl, guid, seenCategories);

            var manifestReport = RefreshManifestRegistration(manifest);

            var sb = new System.Text.StringBuilder();
            sb.Append("csv 成功=").Append(ok).Append(" 失败=").Append(fail);
            sb.Append(" 换掉旧条目=").Append(updated).Append(" 新条目=").Append(added);
            if (removed > 0) sb.Append(" 删掉缺失类目条目=").Append(removed);
            if (shortfall.Count > 0) sb.Append(" 入列不全: ").Append(string.Join("，", shortfall.ToArray()));
            if (string.IsNullOrEmpty(guid)) sb.Append("（没读到 manifest，未分配 slot）");
            if (firstError != null) sb.Append(" 首个错误: ").Append(firstError);
            if (!string.IsNullOrEmpty(manifestReport)) sb.Append("；").Append(manifestReport);
            return sb.ToString();
        }

        /// <summary>manifest 换成新解析出来的对象：Manifests + LoadedManifests（模组列表/缺失提示读它）。</summary>
        static string RefreshManifestRegistration(object manifest)
        {
            try
            {
                var guid = GetGuid(manifest);
                if (string.IsNullOrEmpty(guid) || _manifestsField == null) return null;

                var manifests = _manifestsField.GetValue(null) as IDictionary;
                if (manifests != null) manifests[guid] = manifest;

                var loaded = _loadedManifestsField == null ? null : _loadedManifestsField.GetValue(null) as IList;
                if (loaded != null)
                {
                    bool replaced = false;
                    for (int i = 0; i < loaded.Count; i++)
                    {
                        if (GetGuid(loaded[i]) != guid) continue;
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

        /// <summary>
        /// 用新表替换某个类目的旧数据：删掉这个包的旧条目 → 按新数据补回 → 换 ExternalDataList → 重建解析表。
        /// slot 复用：ResolveInfo 里 Slot=作者 CSV 里的 ID，LocalSlot=游戏内实际分配的 ID。
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
                var p = chaListData.GetType().GetProperty("categoryNo", Any);
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
                var p = chaListData.GetType().GetProperty("dictList", Any);
                return p == null ? null : p.GetValue(chaListData, null) as IDictionary;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static int IntProp(object owner, PropertyInfo property)
        {
            try
            {
                return property == null ? 0 : Convert.ToInt32(property.GetValue(owner, null));
            }
            catch (Exception)
            {
                return 0;
            }
        }

        static void SetIntProp(object owner, PropertyInfo property, int value)
        {
            try
            {
                if (property == null || !property.CanWrite) return;
                property.SetValue(owner, Convert.ChangeType(value, property.PropertyType), null);
            }
            catch (Exception)
            {
            }
        }

        static string StringProp(object owner, PropertyInfo property)
        {
            try
            {
                return property == null ? null : property.GetValue(owner, null) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string ManifestString(object manifest, string property)
        {
            if (manifest == null) return null;
            try
            {
                var p = manifest.GetType().GetProperty(property, Any);
                return p == null ? null : p.GetValue(manifest, null) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- bundle 缓存

        static string GetManifestName()
        {
            var name = _mainManifestField == null ? null : _mainManifestField.GetValue(null) as string;
            return string.IsNullOrEmpty(name) ? "abdata" : name;
        }

        static IEnumerable<string> KeyVariants(string bundleName)
        {
            yield return bundleName;
            if (bundleName.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase))
                yield return bundleName.Substring(0, bundleName.Length - ".unity3d".Length);
            else
                yield return bundleName + ".unity3d";
        }

        /// <summary>
        /// 把 bundle 从 AssetBundleManager 缓存里移除，让下次加载走新 zip 的内容。
        /// Unload(false)：只卸包、不销毁已加载对象，避免场上模型瞬间变粉。
        /// </summary>
        public static bool HardUnloadBundle(string bundleName)
        {
            if (_abmType == null) return false;

            bool released = false;
            try
            {
                var manifest = GetManifestName();

                try
                {
                    var manager = GetSingletonInstance(_abmType);
                    // (bundleName, isUnloadForceRefCount, manifestName, unloadAllLoadedObjects)
                    // 最后一个必须 false，true 会把场上衣服/材质一起销毁
                    if (manager != null && _unloadAssetBundle != null)
                        _unloadAssetBundle.Invoke(manager, new object[] { bundleName, true, manifest, false });
                }
                catch (Exception)
                {
                }

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
                                try { ab.Unload(false); } catch (Exception) { }
                            }
                            released = true;
                        }
                    }

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
            }

            try { ForgetLoadedName(bundleName); } catch (Exception) { }
            return released;
        }

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

        // ---------------------------------------------------------------- 小工具

        /// <summary>Sideloader.ZipArchives 是 Dictionary&lt;string,string&gt;，方向不确定，
        /// key 或 value 等于 guid 就返回另一边。</summary>
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

        static bool SameArchive(string archivePath, string zipPath)
        {
            if (string.IsNullOrEmpty(archivePath) || string.IsNullOrEmpty(zipPath)) return false;

            var a = SafeFullPath(archivePath);
            var b = SafeFullPath(zipPath);
            if (a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

            return string.Equals(Path.GetFileName(archivePath), Path.GetFileName(zipPath),
                StringComparison.OrdinalIgnoreCase);
        }

        static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception) { return null; }
        }

        static Exception Unwrap(Exception e)
        {
            var tie = e as TargetInvocationException;
            return (tie != null && tie.InnerException != null) ? tie.InnerException : e;
        }
    }
}
