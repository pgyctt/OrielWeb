#!/usr/bin/env python3
"""假的通知服务（org.freedesktop.Notifications），供取证脚本使用。

为什么需要一个假的：真实通知守护需要完整桌面会话，而无头 CI 里只跑得动 dbus-run-session。
注册这个最小服务之后，"应用确实把通知发到了会话总线"就成了可断言的事实——连标题与正文
都能逐字符比对，而不是只能看"进程没报错"。

用法（必须在 dbus-run-session 内）：
    dbus-run-session -- bash -c 'python3 tools/fake-notification-service.py & ...'

输出：就绪一行，之后每收到一条通知一行
    FAKE-NOTIFICATION-SERVICE-READY name=org.freedesktop.Notifications
    NOTIFY-CALL app=<app_name> title=<summary> body=<body>
"""

import os
import sys

import dbus
import dbus.mainloop.glib
import dbus.service
from gi.repository import GLib

BUS_NAME = "org.freedesktop.Notifications"
OBJECT_PATH = "/org/freedesktop/Notifications"


class FakeNotifications(dbus.service.Object):
    def __init__(self, bus):
        super().__init__(bus, OBJECT_PATH)

    @dbus.service.method(BUS_NAME, in_signature="susssasa{sv}i", out_signature="u")
    def Notify(self, app_name, replaces_id, app_icon, summary, body, actions, hints, expire_timeout):
        # 逐字段打印：取证脚本据此断言标题与正文在传参路上没有被改写
        print(f"NOTIFY-CALL app={app_name} title={summary} body={body}", flush=True)
        return dbus.UInt32(1)

    @dbus.service.method(BUS_NAME, in_signature="u", out_signature="")
    def CloseNotification(self, notification_id):
        print(f"NOTIFY-CLOSE id={notification_id}", flush=True)

    @dbus.service.method(BUS_NAME, in_signature="", out_signature="as")
    def GetCapabilities(self):
        return ["body"]

    @dbus.service.method(BUS_NAME, in_signature="", out_signature="ssss")
    def GetServerInformation(self):
        return ("oriel-fake", "OrielWeb", "1.0", "1.2")


def main():
    dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
    bus = dbus.SessionBus()

    # 这两个引用必须存活到 mainloop 结束：BusName 一旦被回收，name 就从总线上释放，
    # 调用方随即收到 ServiceUnknown（"was not provided by any .service files"）。
    # 赋值给局部变量即可——main() 阻塞在 run() 里，局部变量就一直活着。
    bus_name = dbus.service.BusName(BUS_NAME, bus)
    service = FakeNotifications(bus)

    print(f"FAKE-NOTIFICATION-SERVICE-READY name={BUS_NAME} pid={os.getpid()}", flush=True)
    _ = (bus_name, service)
    GLib.MainLoop().run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
