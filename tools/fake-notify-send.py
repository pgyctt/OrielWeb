#!/usr/bin/env python3
"""notify-send 的替身：参数形态与真品一致，把调用转到会话总线上的通知服务。

用途：环境里没装 libnotify（因此没有真的 `notify-send`）时，仍能验证"库确实发起了通知"
并检查传参正确——它只把最后那步 D-Bus 调用补齐，命令行解析与真 notify-send 对齐。
装得上 libnotify-bin 时应当优先用真品（取证脚本会自动这么选）。

输出（成功时）：
    FAKE-NOTIFY-SEND app=<app_name> title=<title> body=<body>
失败时向 stderr 打印原因并以非 0 退出——与真 notify-send 的行为一致。
"""

import sys

import dbus

NOTIFICATIONS_BUS = "org.freedesktop.Notifications"
NOTIFICATIONS_PATH = "/org/freedesktop/Notifications"

# 这些选项带一个值（`-t 5000` / `--expire-time=5000` 两种写法都要认）
VALUE_OPTIONS = {"-t", "--expire-time", "-u", "--urgency", "-c", "--category", "-h", "--hint"}
VALUE_OPTION_PREFIXES = ("--expire-time=", "--urgency=", "--category=", "--hint=")


def parse(argv):
    app_name = "OrielWeb"
    icon = ""
    positional = []

    index = 0
    while index < len(argv):
        arg = argv[index]
        if arg.startswith("--app-name="):
            app_name = arg.split("=", 1)[1]
        elif arg.startswith("--icon="):
            icon = arg.split("=", 1)[1]
        elif arg in ("-i", "--icon"):
            index += 1
            icon = argv[index] if index < len(argv) else ""
        elif arg in ("-a", "--app-name"):
            index += 1
            app_name = argv[index] if index < len(argv) else app_name
        elif arg in VALUE_OPTIONS:
            index += 1
        elif arg.startswith(VALUE_OPTION_PREFIXES):
            pass
        else:
            positional.append(arg)
        index += 1

    title = positional[0] if positional else ""
    body = positional[1] if len(positional) > 1 else ""
    return app_name, icon, title, body


def main(argv):
    app_name, icon, title, body = parse(argv)

    bus = dbus.SessionBus()
    notifications = bus.get_object(NOTIFICATIONS_BUS, NOTIFICATIONS_PATH)
    notifications.Notify(
        app_name,
        dbus.UInt32(0),
        icon,
        title,
        body,
        dbus.Array([], signature="s"),
        dbus.Dictionary({}, signature="sv"),
        dbus.Int32(10000),
    )
    print(f"FAKE-NOTIFY-SEND app={app_name} title={title} body={body}", flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Exception as ex:  # noqa: BLE001 - 替身要把任何失败都变成非 0 退出
        print(f"fake-notify-send: {ex}", file=sys.stderr)
        sys.exit(1)
