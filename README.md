# HotReload — 运行时 zipmod 热重载

**v1.0.0 by As-sin** · Koikatu / CharaStudio

不重启游戏，把改好的 `.unity3d` 塞回 zipmod 并立即生效：换包、重建加载器、刷新场上角色的衣服/饰品/头发，一条龙完成。

---

## 1. 支持的两套 Sideloader

插件用反射适配，不编译依赖 `Sideloader.dll`。两套源码结构一致，分别对应两代 Sideloader：

| 项目目录 | 目标环境 | Sideloader 内部结构 | 注册表 |
| --- | --- | --- | --- |
| `HotReload-Madevil` | Madevil / 经典 BepisPlugins（KK 18.x） | `Archives` + `LoadAllUnityArchives` | 按 zip 句柄 |
| `HotReload-RX22` | HF Patch / RX22（2024+） | `Zipmods` + `AddBundles` | 按 `ZipmodInfo.FileName` |

启动日志里的 `就绪（...）` 会列出探测到的接口，缺哪个一眼能看出来。

---

## 2. 安装

```
BepInEx/
├─ plugins/
│  └─ HotReload.Boot.dll          壳：加载/热替换 payload，本身几乎不用更新
└─ hotreload/
   ├─ HotReload.Payload.dll       主体逻辑，日常迭代只换这个
   ├─ hotreload-log.txt           窗口里点"导出"生成
   └─ bundle-cache/               提取出来的 AssetBundle 缓存（可随时删）
```

游戏运行中也能直接覆盖 `HotReload.Payload.dll`，壳检测到文件变化会自动重读；按 `Ctrl+F10` 可强制重读。

---

## 3. 使用流程

1. 进游戏，`Ctrl+F7` 打开窗口（标题：`HotReload 热重载 v1.0.0 by As-sin`）
2. 填 guid（可填多个，空格 / 逗号 / 换行分隔）
3. 点 **释放** —— 摘掉旧注册、卸载旧 bundle、关闭 zip 句柄
4. 用资源管理器把新的 `xxx.unity3d` 覆盖进 zipmod
5. 点 **重载** —— 解析、校验、抽缓存、注册，并自动刷新场上角色

### 快捷键

| 键 | 作用 |
| --- | --- |
| `Ctrl+F7` | 开关窗口 |
| `Enter` | 释放（输入框内） |
| `Ctrl+Enter` | 重载 |
| `Ctrl+F10` | 让壳重新加载 payload（改完插件本体用） |

### 选项

- **表格/manifest**：默认关闭。只有改了 `list/characustom/*.csv` 或 `manifest.xml` 才需要勾上；只改 unity3d 时关着更安全、更快。

---

## 4. 工作原理

### 4.1 释放（Release）

```
按 guid 找到 zipmod 路径
  → 从注册表摘除（Archives / Zipmods + PngList）
  → 卸载该包已加载的 AssetBundle、移除 BundleManager 里的加载器
  → 关闭 Sideloader 持有的 ZipFile
```

先卸加载器再关文件，Unity 的流式读取句柄才会一起释放，否则覆盖时一直是"文件仍被占用"。

### 4.2 重载（Reload）—— 校验通过才提交

```
解析新包（manifest / LoadAllLists / BundleInfos）
  → 逐个 bundle：在 zip 里定位、校验 UnityFS 头、抽到磁盘缓存
  → 全部成功后才：清旧注册表 → 注册新加载器 → 可选合并表格
  → 刷新角色（衣服/饰品/头发）+ MaterialEditor 状态恢复
  → 清理缓存
```

任何一步失败都返回 `失败[...]，kept previous state`，**旧注册表和旧加载器原样保留**，不会再出现"重载失败 = 这个包当场消失"。

### 4.3 为什么要有 bundle-cache

提取出来的 bundle 按内容哈希命名：

```
BepInEx/hotreload/bundle-cache/<sha1>.unity3d
```

加载器是 `() => AssetBundle.LoadFromFile(缓存文件)`，于是：

- Unity 只锁缓存文件，**源 zipmod 随时可覆盖**（释放后一定能解锁）
- 同一个内容重复重载会复用同一份文件，不会无限增长
- 提取时调 `BundleManager.RandomizeCAB`，避免新旧 bundle 的 CAB 撞车

自动清理规则（每次重载后执行）：

- 超过 **15 天**没被用到的文件删除
- 总量超过 **1 GB** 时，从最旧的开始删
- 正在被 Unity 占用的删不掉，自动跳过，留到下次

### 4.4 角色刷新

换完 bundle 后对场景里的角色（Maker 当前角色 / Studio 全部角色 / 全局兜底查找）执行：

```csharp
c.ChangeClothes(true);     // 按当前坐标重建全部衣服部件
c.ChangeAccessory(true);   // 饰品
c.ChangeHair(true);        // 头发
```

不做这一步，角色身上还是旧 prefab（甚至指向已卸载的 bundle），重读卡片就会"衣服全没了"。

MaterialEditor 兼容：`Change*` 之前把控制器标记为 `CoordinateChanging / CharacterLoading / ClothesChanging`，下一帧调 `LoadData(clothes, accessories, hair)` 恢复材质与 shader，最后清标记。没装 ME 就整体跳过。

---

## 5. 日志速查

| 日志片段 | 含义 |
| --- | --- |
| `就绪（AddBundleLoader=OK ...）· build 2026-10-18 22:00:25` | 桥接接口探测结果 + 本次编译时间，用来确认跑的哪一版 |
| `已释放（...）文件可以覆盖` | 句柄都关了，可以去覆盖文件 |
| `文件仍被占用！` | 还有 Unity 句柄没释放，通常是旧加载器没摘干净 |
| `缓存=1` | 成功抽到 1 个 bundle 到磁盘缓存 |
| `注册=缓存1/1` | 1 个 bundle 全部注册成功（缓存加载器） |
| `注册=AddBundles` / `（Sideloader 装载）` | 走了兜底路径 |
| `载入OK name=... 资源数=... 例=...` | 注册后强制加载成功，附带内部 AB 名和资源名 |
| `内部名≠注册名，忽略` | zipmod 打包不规范（AB 名沿用了模板），不注册别名，避免顶掉原版包 |
| `kept previous state` | 新包校验失败，旧状态已保留 |
| `已刷新 N 个角色` | 角色部件重建完成 |
| `bundle 缓存 71MB（上限 1024MB）` | 缓存当前占用 |

---

## 6. 常见问题

**衣服/饰品消失**
多半是 bundle 没真正注册上：看 `注册=` 与 `载入OK`。常见原因：旧加载器没摘干净、`Zipmods` 键写错（HF 家族键是 `FileName`）、bundle 内部 AB 名和 CSV 的 `MainAB` 对不上。

**材质/贴图变默认**
- 检查是否命中过"别名注册"：zipmod 内部 AB 名如果是原版包名（如 `chara/mt_ramp_00.unity3d`），注册别名会把原版包顶掉。当前版本已不再注册别名，重载时会清掉旧别名。
- 检查 MaterialEditor 是否在 `Change*` 期间被清空（需要 `ClothesChanging` 标志位）。

**重载后看不到改动**
角色用的是已实例化的 prefab。重载会自动 `Change*(true)` 重建；若你关掉了刷新或角色不在当前坐标，切一次 outfit 即可。

**列表里没有新衣服**
需要勾选"表格/manifest"再重载，然后切一次分类（或重开捏人界面）。

**文件一直"被占用"**
先点释放；仍占用说明还有 Unity 句柄（旧加载器 / 旧 ABM 缓存），重启游戏一次即可恢复干净状态。

**磁盘占用变大**
`BepInEx/hotreload/bundle-cache` 每改一版内容会多一份（大小=bundle 大小）。关游戏后可直接删整个目录，按 1 GB / 15 天规则也会自动清。

---

## 7. 构建与部署

```powershell
# 自动探测游戏目录，编译 Boot + Payload 并部署
.\build.ps1

# 只更新壳
.\build.ps1 -BootOnly

# 指定游戏目录
.\build.ps1 -GameDir 'E:\game\GDAkk\Koikatu'
```

- 编译器：系统自带 `csc.exe`（`.NET Framework 4.x`），引用游戏自带的 `mscorlib / UnityEngine / Assembly-CSharp / BepInEx / 0Harmony`
- 每次编译会生成 `Payload/BuildStamp.g.cs`，启动日志里的 `build ...` 就是它
- 部署目标：`BepInEx/plugins/HotReload.Boot.dll`、`BepInEx/hotreload/HotReload.Payload.dll`

### 源码结构

```
Boot/Boot.cs                     壳：Assembly.Load(bytes) + 变更检测 + 快捷键
Payload/Entry.cs                 入口：OnLoad / OnUnload
Payload/PayloadBehaviour.cs      IMGUI 窗口与按键
Payload/ContentHotReload.cs      命令编排、日志、角色刷新、MaterialEditor 桥
Payload/SideloaderBridge.cs      反射桥：注册表、加载器、缓存、表格
tools/dump-api.ps1               用 Mono.Cecil 反编译目标 DLL 的接口（排查版本差异用）
```

---

## 8. 已知限制

- 不重载卡片、坐标卡、当前 Studio 场景；只处理 zipmod 资源
- 默认只换 `.unity3d`，CSV/manifest 需手动勾选
- 不做「槽位身份保留」：如果新版 zipmod 改了 CSV 里的 ID/顺序，旧卡片引用会漂移，需要重新选一次
- zipmod 自身打包问题（AB 名不一致、组件配置错误）不会被修正，只在日志里提示
- Unity 已加载的缓存文件无法删除/覆盖，必须走"释放 → 覆盖 → 重载"或重启

---

## 9. 许可协议

本项目采用 **GNU General Public License v3.0** 发布，完整条款见 [LICENSE](LICENSE)。

```
HotReload - Koikatu runtime zipmod hot reload
Copyright (C) 2026 As-sin

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License version 3 as
published by the Free Software Foundation.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
```

源码文件均带 `SPDX-License-Identifier: GPL-3.0-only` 头。

实现思路参考了 BepisPlugins 的 Sideloader 热重载分支（LGPL-3.0），**未直接包含其源码**；`AssetBundle` 接口通过反射调用，不静态链接 Sideloader。

---

*v1.0.0 by As-sin*
