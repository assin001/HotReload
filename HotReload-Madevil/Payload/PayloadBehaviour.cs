// SPDX-License-Identifier: GPL-3.0-only
// HotReload - Koikatu runtime zipmod hot reload
// Copyright (C) 2026 As-sin
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 3 as
// published by the Free Software Foundation. See LICENSE for details.
using System;
using UnityEngine;

namespace HotReload.Payload
{
    /// <summary>
    /// 界面与按键。Ctrl+F7 开关窗口。
    /// 属于"可丢弃"部分，payload 重载时整个重建。
    /// 布局：自绘标题栏 + 大块日志区 + 底部操作行。
    /// </summary>
    internal class PayloadBehaviour : MonoBehaviour
    {
        const int WindowId = 0x48524C44;
        const float WindowWidth = 440f;
        const float WindowHeight = 512f;
        const float TitleHeight = 26f;
        const float Pad = 8f;

        static Font _font;

        Rect _rect = new Rect(90f, 60f, WindowWidth, WindowHeight);
        bool _visible;
        bool _collapsed;
        bool _dragging;
        bool _guiErrorReported;
        string _guidText = "";
        Vector2 _scroll;
        int _lastLogCount;

        GUIStyle _windowStyle, _titleStyle, _labelStyle, _hintStyle, _boxStyle, _smallStyle, _buttonStyle, _fieldStyle,
            _toggleStyle;
        Texture2D _texWindow, _texBox, _texButton, _texButtonHover, _texButtonActive, _texField;

        void Awake()
        {
            // 刷新角色后要用协程等一帧，再让 MaterialEditor 恢复材质数据
            ContentHotReload.CoroutineStarter = StartCoroutine;
        }

        void Update()
        {
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.F7))
            {
                _visible = !_visible;
                _guiErrorReported = false;
                if (!_visible)
                {
                    _dragging = false;
                    InputBlock.Clear();
                }
            }

            InputBlock.Visible = _visible;
            InputBlock.WindowRect = _rect;
            InputBlock.Dragging = _dragging;

            if (!_visible) return;

            // 键盘兜底（文本框获得焦点时不抢键）：Enter 释放，Ctrl+Enter 重载
            if (GUIUtility.keyboardControl == 0 && Input.GetKeyDown(KeyCode.Return))
            {
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                    ContentHotReload.Reload();
                else if (!string.IsNullOrEmpty(_guidText))
                    ContentHotReload.Release(_guidText);
            }
        }

        void OnGUI()
        {
            if (!_visible) return;

            // 用 IMGUI 事件里的真实鼠标位置喂给 InputBlock，
            // 否则 mousePosition 被冻结后就没法判断鼠标什么时候离开窗口
            var ev = Event.current;
            if (ev != null && ev.type != EventType.Used)
                InputBlock.RawMouse = new Vector3(ev.mousePosition.x, Screen.height - ev.mousePosition.y, 0f);

            EnsureStyle();
            var oldFont = GUI.skin.font;
            if (_font != null) GUI.skin.font = _font;

            try
            {
                _rect = GUI.Window(WindowId, _rect, DrawWindow, "", _windowStyle);
                InputBlock.WindowRect = _rect;
            }
            catch (Exception e)
            {
                if (!_guiErrorReported)
                {
                    _guiErrorReported = true;
                    ContentHotReload.SayErr("界面异常: " + e);
                }
            }
            finally
            {
                GUI.skin.font = oldFont;
            }
        }

        void DrawWindow(int id)
        {
            // ---------- 标题栏 ----------
            var titleRect = new Rect(1f, 1f, _rect.width - 2f, TitleHeight);
            GUI.Label(titleRect, "HotReload  热重载  <size=10><color=#9aa0a6>v1.0.0 by As-sin</color></size>", _titleStyle);

            if (GUI.Button(new Rect(_rect.width - 50f, 4f, 20f, 18f), _collapsed ? "+" : "-", _smallStyle))
            {
                _collapsed = !_collapsed;
                _rect.height = _collapsed ? TitleHeight + 2f : WindowHeight;
            }

            if (GUI.Button(new Rect(_rect.width - 27f, 4f, 20f, 18f), "x", _smallStyle))
            {
                _visible = false;
                _dragging = false;
                InputBlock.Clear();
                return;
            }

            // 只允许拖标题栏，避免拖窗口时把模型一起拖了
            var e = Event.current;
            if (e.type == EventType.MouseDown && titleRect.Contains(e.mousePosition)) _dragging = true;
            else if (e.type == EventType.MouseUp) _dragging = false;
            InputBlock.Dragging = _dragging;

            if (!_collapsed)
            {
                GUI.DragWindow(titleRect);

                // ---------- 主体 ----------
                float bodyTop = TitleHeight + Pad;
                float bodyHeight = _rect.height - bodyTop - Pad;
                if (bodyHeight < 120f) bodyHeight = 120f;

                GUILayout.BeginArea(new Rect(Pad, bodyTop, _rect.width - Pad * 2f, bodyHeight));

                // 日志区
                GUILayout.BeginVertical(_boxStyle, GUILayout.ExpandHeight(true));
                _scroll = GUILayout.BeginScrollView(_scroll);
                var logs = ContentHotReload.Logs;
                for (int i = 0; i < logs.Count; i++)
                    GUILayout.Label(logs[i], _labelStyle);
                GUILayout.EndScrollView();
                GUILayout.EndVertical();

                // 内容包
                GUILayout.Space(4f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("内容包", _hintStyle, GUILayout.Width(56f));
                _guidText = GUILayout.TextField(_guidText == null ? "" : _guidText, _fieldStyle,
                    GUILayout.Height(22f), GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                // 选项：默认只重载 unity3d
                GUILayout.Space(2f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("选项", _hintStyle, GUILayout.Width(56f));
                ContentHotReload.ReloadLists = GUILayout.Toggle(ContentHotReload.ReloadLists,
                    " 表格/manifest", _toggleStyle, GUILayout.Height(20f));
                GUILayout.EndHorizontal();

                // 操作
                GUILayout.Space(4f);
                GUILayout.BeginHorizontal();
                GUILayout.Label("操作", _hintStyle, GUILayout.Width(56f));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("释放", _buttonStyle, GUILayout.Width(72f), GUILayout.Height(26f)))
                    ContentHotReload.Release(_guidText);
                if (GUILayout.Button("重载", _buttonStyle, GUILayout.Width(72f), GUILayout.Height(26f)))
                    ContentHotReload.Reload();
                if (GUILayout.Button("清空", _buttonStyle, GUILayout.Width(72f), GUILayout.Height(26f)))
                {
                    ContentHotReload.ClearLog();
                    _lastLogCount = 0;
                }
                if (GUILayout.Button("导出", _buttonStyle, GUILayout.Width(72f), GUILayout.Height(26f)))
                    ContentHotReload.ExportLog();
                GUILayout.EndHorizontal();

                GUILayout.EndArea();
            }

            // 日志自动滚到底
            if (ContentHotReload.LogCount != _lastLogCount)
            {
                _lastLogCount = ContentHotReload.LogCount;
                _scroll.y = float.MaxValue;
            }
        }

        void EnsureStyle()
        {
            if (_font == null)
            {
                try
                {
                    _font = Font.CreateDynamicFontFromOSFont(
                        new[] { "Microsoft YaHei", "SimHei", "Meiryo", "Arial Unicode MS", "Arial" }, 13);
                }
                catch (Exception)
                {
                    _font = null;
                }
            }

            if (_windowStyle != null) return;

            var line = new Color(0.32f, 0.32f, 0.35f, 1f);

            _texWindow = MakeBox(new Color(0.14f, 0.14f, 0.15f, 0.98f), line);
            _texBox = MakeBox(new Color(0.06f, 0.06f, 0.07f, 1f), line);
            _texButton = MakeBox(new Color(0.21f, 0.21f, 0.23f, 1f), line);
            _texButtonHover = MakeBox(new Color(0.29f, 0.29f, 0.32f, 1f), line);
            _texButtonActive = MakeBox(new Color(0.37f, 0.37f, 0.41f, 1f), line);
            _texField = MakeBox(new Color(0.06f, 0.06f, 0.07f, 1f), line);

            _windowStyle = new GUIStyle(GUI.skin.box);
            _windowStyle.normal.background = _texWindow;
            _windowStyle.border = new RectOffset(1, 1, 1, 1);
            _windowStyle.padding = new RectOffset(0, 0, 0, 0);
            _windowStyle.margin = new RectOffset(0, 0, 0, 0);

            // 标题栏底色
            var titleBg = MakeBox(new Color(0.19f, 0.19f, 0.21f, 1f), line);
            _titleStyle = new GUIStyle(GUI.skin.label);
            _titleStyle.normal.background = titleBg;
            _titleStyle.normal.textColor = new Color(0.92f, 0.92f, 0.94f, 1f);
            _titleStyle.alignment = TextAnchor.MiddleCenter;
            _titleStyle.fontStyle = FontStyle.Bold;
            _titleStyle.richText = true;
            _titleStyle.border = new RectOffset(1, 1, 1, 1);

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.normal.textColor = new Color(0.82f, 0.82f, 0.85f, 1f);
            _labelStyle.wordWrap = true;
            _labelStyle.fontSize = 12;

            _hintStyle = new GUIStyle(GUI.skin.label);
            _hintStyle.normal.textColor = new Color(0.70f, 0.70f, 0.74f, 1f);
            _hintStyle.alignment = TextAnchor.MiddleLeft;

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = _texBox;
            _boxStyle.border = new RectOffset(1, 1, 1, 1);
            _boxStyle.padding = new RectOffset(6, 6, 6, 6);
            _boxStyle.margin = new RectOffset(0, 0, 0, 0);

            _smallStyle = new GUIStyle(GUI.skin.button);
            _smallStyle.normal.background = _texButton;
            _smallStyle.hover.background = _texButtonHover;
            _smallStyle.active.background = _texButtonActive;
            _smallStyle.normal.textColor = new Color(0.88f, 0.88f, 0.90f, 1f);
            _smallStyle.hover.textColor = Color.white;
            _smallStyle.active.textColor = Color.white;
            _smallStyle.alignment = TextAnchor.MiddleCenter;
            _smallStyle.fontSize = 12;
            _smallStyle.padding = new RectOffset(0, 0, 0, 0);
            _smallStyle.border = new RectOffset(1, 1, 1, 1);
            _smallStyle.margin = new RectOffset(0, 0, 0, 0);

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.normal.background = _texButton;
            _buttonStyle.hover.background = _texButtonHover;
            _buttonStyle.active.background = _texButtonActive;
            _buttonStyle.normal.textColor = new Color(0.88f, 0.88f, 0.90f, 1f);
            _buttonStyle.hover.textColor = Color.white;
            _buttonStyle.active.textColor = Color.white;
            _buttonStyle.alignment = TextAnchor.MiddleCenter;
            _buttonStyle.fontSize = 12;
            _buttonStyle.border = new RectOffset(1, 1, 1, 1);

            _fieldStyle = new GUIStyle(GUI.skin.textField);
            _fieldStyle.normal.background = _texField;
            _fieldStyle.focused.background = _texField;
            _fieldStyle.normal.textColor = new Color(0.90f, 0.90f, 0.92f, 1f);
            _fieldStyle.focused.textColor = Color.white;
            _fieldStyle.padding = new RectOffset(6, 6, 4, 4);
            _fieldStyle.border = new RectOffset(1, 1, 1, 1);

            _toggleStyle = new GUIStyle(GUI.skin.toggle);
            _toggleStyle.normal.textColor = new Color(0.82f, 0.82f, 0.85f, 1f);
            _toggleStyle.onNormal.textColor = new Color(0.90f, 0.90f, 0.92f, 1f);
            _toggleStyle.hover.textColor = Color.white;
            _toggleStyle.onHover.textColor = Color.white;
            _toggleStyle.fontSize = 12;
        }

        static Texture2D MakeBox(Color fill, Color border)
        {
            var t = new Texture2D(3, 3, TextureFormat.RGBA32, false);
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 3; x++)
                    t.SetPixel(x, y, (x == 0 || y == 0 || x == 2 || y == 2) ? border : fill);
            }
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            t.Apply();
            return t;
        }
    }
}
