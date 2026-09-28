#!/usr/bin/env python3
"""
verify-webview2-slots.py — 校验 OrielWeb 手工互操作层的 vtable 槽位硬编码。

背景
----
OrielWeb 的 Windows 平台层绕开 ComWrappers，手工按 vtable 槽位直调 WebView2
（src/OrielWeb/Platform/Windows/Interop/WebView2Com.cs）。槽位是人工从官方
WebView2.h 数出来的，**编译器无法校验**，数错一格就是 AV 或静默调用错函数。

本脚本自动完成这件事：解析官方 WebView2.h 的真实 vtable 布局，与 C# 代码里
的 WebView2Slots 常量逐项比对。

什么时候跑
----------
  - 升级 Microsoft.Web.WebView2 包版本后
  - 新增任何 Vtable[WebView2Slots.XXX] 调用后
  - CI 里作为门禁（建议加进 aot-windows job）

用法
----
  python3 verify-webview2-slots.py --repo /path/to/OrielWeb
  python3 verify-webview2-slots.py --repo . --version 1.0.2903.40
  python3 verify-webview2-slots.py --repo . --pkg /path/to/Microsoft.Web.WebView2.nupkg

退出码：0 = 全部一致；1 = 有不匹配；2 = 环境/参数错误。
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

# ---------------------------------------------------------------------------
# 槽位常量 → (WebView2 接口, 方法名)
# 新增 Vtable[WebView2Slots.XXX] 时必须在此登记，否则脚本会报"未登记"。
# ---------------------------------------------------------------------------
SLOT_REGISTRY = {
    "Environment_CreateController":                    ("ICoreWebView2Environment", "CreateCoreWebView2Controller"),
    "Controller_put_IsVisible":                        ("ICoreWebView2Controller", "put_IsVisible"),
    "Controller_put_Bounds":                           ("ICoreWebView2Controller", "put_Bounds"),
    "Controller_NotifyParentWindowPositionChanged":    ("ICoreWebView2Controller", "NotifyParentWindowPositionChanged"),
    "Controller_get_CoreWebView2":                     ("ICoreWebView2Controller", "get_CoreWebView2"),
    "WebView2_get_Settings":                           ("ICoreWebView2", "get_Settings"),
    "WebView2_Navigate":                               ("ICoreWebView2", "Navigate"),
    "WebView2_add_NavigationCompleted":                ("ICoreWebView2", "add_NavigationCompleted"),
    "WebView2_AddScriptToExecuteOnDocumentCreated":    ("ICoreWebView2", "AddScriptToExecuteOnDocumentCreated"),
    "WebView2_ExecuteScript":                          ("ICoreWebView2", "ExecuteScript"),
    "WebView2_PostWebMessageAsJson":                   ("ICoreWebView2", "PostWebMessageAsJson"),
    "WebView2_add_WebMessageReceived":                 ("ICoreWebView2", "add_WebMessageReceived"),
    "WebView2_add_DocumentTitleChanged":               ("ICoreWebView2", "add_DocumentTitleChanged"),
    "WebView2_get_DocumentTitle":                      ("ICoreWebView2", "get_DocumentTitle"),
    "Settings_put_AreDevToolsEnabled":                 ("ICoreWebView2Settings", "put_AreDevToolsEnabled"),
    "Env3_SetVirtualHostNameToFolderMapping":          ("ICoreWebView2_3", "SetVirtualHostNameToFolderMapping"),
    "WebMessageArgs_get_WebMessageAsJson":             ("ICoreWebView2WebMessageReceivedEventArgs", "get_WebMessageAsJson"),
    "NavCompletedArgs_get_IsSuccess":                  ("ICoreWebView2NavigationCompletedEventArgs", "get_IsSuccess"),
}

DEFAULT_VERSION = "1.0.2903.40"
SLOT_SOURCE = os.path.join("src", "OrielWeb", "Platform", "Windows", "Interop", "WebView2Com.cs")


# ---------------------------------------------------------------------------
# 1) 取得 WebView2.h
# ---------------------------------------------------------------------------
def fetch_package(version, workdir):
    """下载 nuget 包并解压，返回解压目录。"""
    nupkg = os.path.join(workdir, f"Microsoft.Web.WebView2.{version}.nupkg")
    url = f"https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/{version}"
    print(f"[1/3] 下载 Microsoft.Web.WebView2 {version} ...")
    subprocess.run(["curl", "-sSL", "-o", nupkg, url], check=True)
    if not zipfile.is_zipfile(nupkg):
        sys.exit(f"下载结果不是 zip：{nupkg}")
    extracted = os.path.join(workdir, "pkg")
    with zipfile.ZipFile(nupkg) as z:
        z.extractall(extracted)
    return extracted


def unpack_local(pkg_path, workdir):
    print(f"[1/3] 解压本地包 {pkg_path} ...")
    extracted = os.path.join(workdir, "pkg")
    with zipfile.ZipFile(pkg_path) as z:
        z.extractall(extracted)
    return extracted


def find_header(pkg_dir):
    for root, _dirs, files in os.walk(pkg_dir):
        for f in files:
            if f == "WebView2.h":
                return os.path.join(root, f)
    sys.exit("在包内未找到 WebView2.h（该包版本可能不含原生头文件）")


# ---------------------------------------------------------------------------
# 2) 解析 WebView2.h，还原真实 vtable 布局
# ---------------------------------------------------------------------------
def parse_vtable(header_path):
    """
    返回 {接口名: [方法名...]}（已按 vtable 顺序展开基接口方法）。
    MIDL 生成的 C++ 头里，接口体的虚函数声明顺序 == vtable 布局顺序。
    """
    src = open(header_path, encoding="utf-8", errors="replace").read()

    # 用花括号配对截取接口体（不能靠 '};' 正则：会漏/截断）
    ifaces = {}
    for m in re.finditer(r'MIDL_INTERFACE\(\s*"([^"]+)"\s*\)', src):
        sig = re.match(r'\s*([A-Za-z_][A-Za-z0-9_]*)\s*:\s*public\s+([A-Za-z0-9_]+)\s*\{',
                       src[m.end():])
        if not sig:
            continue
        name, base = sig.group(1), sig.group(2)
        k = m.end() + sig.end()
        depth, p = 1, k
        while p < len(src) and depth:
            if src[p] == '{':
                depth += 1
            elif src[p] == '}':
                depth -= 1
            p += 1
        body = src[k:p - 1]
        # 关键：容忍 `virtual /* [propget] */ HRESULT STDMETHODCALLTYPE X(` 这类注释，
        # 否则属性访问器会被整批漏掉，导致后续槽位整体偏移。
        methods = re.findall(
            r'virtual\s+(?:/\*.*?\*/\s*)*HRESULT\s+STDMETHODCALLTYPE\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(',
            body)
        ifaces[name] = {"base": base, "methods": methods}

    flat_cache = {}

    def flatten(name, _depth=0):
        if name in flat_cache:
            return flat_cache[name]
        if name not in ifaces or _depth > 64:
            return []
        i = ifaces[name]
        seq = flatten(i["base"], _depth + 1) + [(x, name) for x in i["methods"]]
        flat_cache[name] = seq
        return seq

    return {n: flatten(n) for n in ifaces}, ifaces


def real_slot(vtable, iface, method):
    """槽位(0 基) = 3（IUnknown 的 QI/AddRef/Release）+ 接口内序数(0 基)"""
    for idx, (m, _owner) in enumerate(vtable.get(iface, [])):
        if m == method:
            return 3 + idx
    return None


# ---------------------------------------------------------------------------
# 3) 读取 C# 里的槽位常量并比对
# ---------------------------------------------------------------------------
def read_declared(repo, label):
    path = os.path.join(repo, SLOT_SOURCE)
    if not os.path.isfile(path):
        sys.exit(f"找不到 {path}（用 --repo 指定 OrielWeb 仓库根目录）")
    src = open(path, encoding="utf-8", errors="replace").read()
    declared = {n: int(v) for n, v in
                re.findall(r'public\s+const\s+int\s+(\w+)\s*=\s*(\d+)\s*;', src)}
    # 代码里实际被 Vtable[...] 使用的槽位常量
    used = set(re.findall(r'Vtable\[\s*WebView2Slots\.(\w+)\s*\]', src))
    print(f"[2/3] 读取 {label}：声明 {len(declared)} 个常量，其中 {len(used)} 个被 Vtable[] 实际使用")
    return declared, used


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default=".", help="OrielWeb 仓库根目录")
    ap.add_argument("--version", default=DEFAULT_VERSION, help="Microsoft.Web.WebView2 包版本")
    ap.add_argument("--pkg", help="改用本地 .nupkg，避免联网")
    args = ap.parse_args()

    workdir = tempfile.mkdtemp(prefix="wv2slots-")
    try:
        pkg_dir = unpack_local(args.pkg, workdir) if args.pkg else fetch_package(args.version, workdir)
        header = find_header(pkg_dir)
        vtable, ifaces = parse_vtable(header)

        # 解析自检：这几个数字是 WebView2 接口的公开事实，对不上说明解析器坏了
        print(f"[3/3] 解析 {os.path.basename(header)}：识别 {len(ifaces)} 个接口")
        for name, expect in (("ICoreWebView2", 58), ("ICoreWebView2_2", 7), ("ICoreWebView2_3", 5)):
            got = len(ifaces.get(name, {}).get("methods", []))
            mark = "OK " if got == expect else "!! "
            print(f"        {mark}{name}: {got} 个自有方法（应为 {expect}）")
            if got != expect:
                sys.exit("解析自检失败——头文件格式可能已变化，请检查解析正则")

        declared, used = read_declared(args.repo, "WebView2Com.cs")

        print()
        print(f"{'常量':<46}{'接口':<44}{'方法':<40}{'代码':>5}{'实际':>6}  结果")
        print("-" * 146)

        bad, unchecked_missing = 0, []
        for const, (iface, method) in SLOT_REGISTRY.items():
            code = declared.get(const)
            actual = real_slot(vtable, iface, method)
            if code is None:
                print(f"{const:<46}{iface:<44}{method:<40}{'--':>5}{str(actual):>6}  !! 代码中已无此常量")
                bad += 1
                continue
            ok = code == actual
            if not ok:
                bad += 1
            print(f"{const:<46}{iface:<44}{method:<40}{code:>5}{str(actual):>6}  "
                  f"{'OK' if ok else '<<< 不匹配（会调错函数/AV）'}")
        print("-" * 146)

        # 反向检查：代码里用了但注册表没登记的槽位 —— 意味着脚本无法校验它
        unknown = sorted(used - set(SLOT_REGISTRY))
        if unknown:
            print(f"\n!! 以下槽位常量被 Vtable[] 使用但未在 SLOT_REGISTRY 登记，"
                  f"脚本无法校验（请补充登记）：")
            for u in unknown:
                print(f"     {u} = {declared.get(u)}")

        print()
        if bad == 0 and not unknown:
            print(f"结果：全部一致（{len(SLOT_REGISTRY)} 项）—— 当前槽位与 WebView2 "
                  f"{args.version} 的 ABI 布局吻合。")
            return 0
        print(f"结果：{bad} 项不匹配，{len(unknown)} 项未登记 —— 必须先修正再发布。")
        return 1
    finally:
        shutil.rmtree(workdir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
