// SPDX-License-Identifier: GPL-3.0-only
// HotReload - Koikatu runtime zipmod hot reload
// Copyright (C) 2026 As-sin
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 3 as
// published by the Free Software Foundation. See LICENSE for details.
using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace HotReload.Boot
{
    /// <summary>
    /// 固定不动的壳，编译一次之后不再替换。
    /// 职责：撤掉旧补丁 -> 让旧 payload 自我清理 -> 从磁盘读字节加载新 payload -> 调用 OnLoad。
    /// 不引用 Payload 工程，两边只靠类型名 + 静态方法签名约定。
    /// </summary>
    [BepInPlugin(Guid, "HotReload Boot", "1.0.0")]
    public class Boot : BaseUnityPlugin
    {
        public const string Guid = "as-sin.hotreload.boot";

        /// <summary>payload 里的 Harmony 实例必须用同一个 ID，否则 UnpatchAll 撤不掉。</summary>
        public const string PayloadHarmonyId = "as-sin.hotreload.payload";

        const string PayloadTypeName = "HotReload.Payload.Entry";

        ConfigEntry<KeyboardShortcut> _reloadKey;
        ConfigEntry<bool> _autoReload;

        string _payloadPath;
        DateTime _lastWriteUtc;

        Assembly _payloadAssembly;
        Action _onLoad;
        Action _onUnload;

        void Awake()
        {
            _reloadKey = Config.Bind("General", "ReloadKey", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl),
                "重新读取 payload 的快捷键（和 payload 内部的热重载键分开）");
            _autoReload = Config.Bind("General", "AutoReload", true,
                "payload dll 一有变化就自动重载");

            _payloadPath = Path.Combine(Path.Combine(Paths.BepInExRootPath, "hotreload"), "HotReload.Payload.dll");
            Logger.LogInfo("payload 路径: " + _payloadPath);

            Reload();
        }

        void Update()
        {
            if (_reloadKey.Value.IsDown())
            {
                Reload();
                return;
            }

            if (!_autoReload.Value) return;

            try
            {
                if (!File.Exists(_payloadPath)) return;
                var write = File.GetLastWriteTimeUtc(_payloadPath);
                if (write == _lastWriteUtc) return;
                _lastWriteUtc = write;
                Reload();
            }
            catch (IOException)
            {
            }
        }

        void Reload()
        {
            UnloadPayload();

            try
            {
                if (!File.Exists(_payloadPath))
                {
                    Logger.LogWarning("payload 不存在: " + _payloadPath);
                    return;
                }

                var bytes = File.ReadAllBytes(_payloadPath);
                _lastWriteUtc = File.GetLastWriteTimeUtc(_payloadPath);

                _payloadAssembly = Assembly.Load(bytes);

                var entry = _payloadAssembly.GetType(PayloadTypeName, true);
                _onLoad = Bind(entry, "OnLoad");
                _onUnload = Bind(entry, "OnUnload");

                _onLoad();
                Logger.LogInfo("payload 已加载: " + _payloadAssembly.GetName().Version);
            }
            catch (Exception e)
            {
                Logger.LogError("payload 加载失败: " + e);
                _onLoad = null;
                _onUnload = null;
                _payloadAssembly = null;
            }
        }

        static Action Bind(Type entry, string methodName)
        {
            var mi = entry.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (mi == null)
                throw new MissingMethodException(entry.FullName + "." + methodName + "()");
            return (Action)Delegate.CreateDelegate(typeof(Action), mi);
        }

        void UnloadPayload()
        {
            try
            {
                new Harmony(PayloadHarmonyId).UnpatchSelf();
            }
            catch (Exception e)
            {
                Logger.LogError("撤销补丁失败: " + e);
            }

            try
            {
                if (_onUnload != null) _onUnload();
            }
            catch (Exception e)
            {
                Logger.LogError("payload 清理失败: " + e);
            }

            _onLoad = null;
            _onUnload = null;
            _payloadAssembly = null;
        }

        void OnDestroy()
        {
            UnloadPayload();
        }
    }
}
