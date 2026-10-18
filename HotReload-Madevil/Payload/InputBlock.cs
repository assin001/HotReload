// SPDX-License-Identifier: GPL-3.0-only
// HotReload - Koikatu runtime zipmod hot reload
// Copyright (C) 2026 As-sin
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 3 as
// published by the Free Software Foundation. See LICENSE for details.
using HarmonyLib;
using UnityEngine;

namespace HotReload.Payload
{
    /// <summary>
    /// 窗口打开且鼠标压在窗口上（或正在拖标题栏）时，屏蔽游戏对鼠标的读取。
    /// Studio 里拖人物/转视角，除了 GetMouseButton 和 GetAxis 之外还会直接读
    /// Input.mousePosition 算位移，所以只挡按钮和轴是不够的，必须把 mousePosition 也冻住。
    /// </summary>
    internal static class InputBlock
    {
        public static bool Visible;
        public static bool Dragging;

        /// <summary>窗口矩形（屏幕坐标，Y 轴朝上，和 Input.mousePosition 同一套坐标）。</summary>
        public static Rect WindowRect;

        /// <summary>真实鼠标位置（屏幕坐标）。由 get_mousePosition 后置补丁和 OnGUI 事件共同更新。</summary>
        public static Vector3 RawMouse;

        static Vector3 _frozen;
        static bool _hasFrozen;

        public static bool Blocking
        {
            get
            {
                if (!Visible) return false;
                if (Dragging) return true;

                // 换成 GUI 坐标（Y 轴朝下）再判断是否落在窗口里
                var gui = new Vector2(RawMouse.x, Screen.height - RawMouse.y);
                return WindowRect.Contains(gui);
            }
        }

        public static void Clear()
        {
            Visible = false;
            Dragging = false;
            _hasFrozen = false;
        }

        /// <summary>返回 true 表示已经改写结果、跳过原方法。</summary>
        internal static bool FreezeMouse(ref Vector3 result)
        {
            if (!Blocking)
            {
                _hasFrozen = false;
                return false;
            }

            if (!_hasFrozen)
            {
                _frozen = RawMouse;
                _hasFrozen = true;
            }

            result = _frozen;
            return true;
        }
    }

    [HarmonyPatch(typeof(Input), "get_mousePosition")]
    internal static class Patch_MousePosition
    {
        static bool Prefix(ref Vector3 __result)
        {
            return !InputBlock.FreezeMouse(ref __result);
        }

        static void Postfix(ref Vector3 __result)
        {
            // 前缀跳过原方法时 Blocking 必为真，这里不会污染 RawMouse
            if (!InputBlock.Blocking) InputBlock.RawMouse = __result;
        }
    }

    [HarmonyPatch(typeof(Input), "get_mouseScrollDelta")]
    internal static class Patch_MouseScrollDelta
    {
        static bool Prefix(ref Vector2 __result)
        {
            if (!InputBlock.Blocking) return true;
            __result = Vector2.zero;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), "GetMouseButton")]
    internal static class Patch_GetMouseButton
    {
        static bool Prefix(ref bool __result)
        {
            if (!InputBlock.Blocking) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), "GetMouseButtonDown")]
    internal static class Patch_GetMouseButtonDown
    {
        static bool Prefix(ref bool __result)
        {
            if (!InputBlock.Blocking) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), "GetMouseButtonUp")]
    internal static class Patch_GetMouseButtonUp
    {
        static bool Prefix(ref bool __result)
        {
            if (!InputBlock.Blocking) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), "GetAxis")]
    internal static class Patch_GetAxis
    {
        static bool Prefix(ref float __result)
        {
            if (!InputBlock.Blocking) return true;
            __result = 0f;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), "GetAxisRaw")]
    internal static class Patch_GetAxisRaw
    {
        static bool Prefix(ref float __result)
        {
            if (!InputBlock.Blocking) return true;
            __result = 0f;
            return false;
        }
    }
}
