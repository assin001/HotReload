// SPDX-License-Identifier: GPL-3.0-only
// HotReload - Koikatu runtime zipmod hot reload
// Copyright (C) 2026 As-sin
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 3 as
// published by the Free Software Foundation. See LICENSE for details.
using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HotReload.Payload
{
    /// <summary>
    /// 壳唯一认的入口：类型名 HotReload.Payload.Entry，两个公开静态无参方法 OnLoad / OnUnload。
    /// </summary>
    public static class Entry
    {
        static GameObject _root;

        public static void OnLoad()
        {
            var log = BepInEx.Logging.Logger.CreateLogSource("HotReload.Payload");
            log.LogInfo("payload 载入，程序集版本 " + typeof(Entry).Assembly.GetName().Version);

            // 必须和壳里的 Boot.PayloadHarmonyId 一致，壳重载时会按这个 ID 撤销
            try { new Harmony("as-sin.hotreload.payload").PatchAll(typeof(Entry).Assembly); }
            catch (Exception e) { log.LogError("打补丁失败: " + e); }

            ContentHotReload.Initialize(log);

            _root = new GameObject("HotReload.Payload");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.AddComponent<PayloadBehaviour>();
        }

        public static void OnUnload()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }
    }
}
