# WxToastNotifier v2.2.1 — 微信 4.2 专属消息 Toast 通知

一个 Windows 10/11 下把 **微信新消息** 转到 **系统 Toast 通知** 的小工具。
不抢微信窗口，不重复推送自己发的消息，不推公众号文章，通知里带 **联系人圆形头像**。
微信 4.1 / 4.2 已经适配，WCDB 密钥直接从微信进程内存提取，**不需要 passphrase，不需要 all_keys.json**。

[![GitHub stars](https://img.shields.io/github/stars/TerminatorForMHT/wechat-message-assistant-windows?style=social)](https://github.com/TerminatorForMHT/wechat-message-assistant-windows)

## 下载

| 文件 | 说明 |
|------|------|
| **WxToastNotifier_2.2.1.0_x64.msix** | 安装包，直接双击安装 |
| **Install.ps1** | 一键安装脚本（含证书导入） |
| **WxToastNotifier.cer** | 自签名证书（安装前可选导入受信任根） |

## 特性

- 🔔 **系统级 Toast 通知**，带圆形联系人头像
- 🧠 **自动识别自己发出的消息**，不重复推送
- 🔕 **免打扰会话自动跳过**
- 🕑 **微信启动后的历史同步消息自动过滤**
- 🚫 跳过 `brandsessionholder` 聚合会话，**不推公众号文章**、不推服务号重复
- 🔑 **WCDB 密钥内存提取**，无需手动配置 passphrase / all_keys.json
- 👤 **头像本地缓存 + 后台并发下载**，推送零额外耗时
- 🪟 **WinUI 3 / Windows App SDK / H.NotifyIcon** 构建，单实例，托盘常驻

## 安装

双击 `.msix` 即可。首次安装如果提示证书不受信任：

```powershell
# 管理员 PowerShell
Import-Certificate .\WxToastNotifier.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate .\WxToastNotifier.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

或直接运行仓库里的 `Install.ps1`。

## 使用

1. 启动微信（必须，工具会扫描微信进程拿到数据库密钥）
2. 运行 WxToastNotifier
3. 点「开始监听」→ 工具会自动扫描 `db_storage`、解密数据库副本到临时目录、建立会话基线
4. 托盘图标常驻，退出微信后工具会停止推送

## 工作原理（给爱折腾的人）

微信 4.0 起聊天记录在 `db_storage` 里用 WCDB 加密，`message/message_*.db`、`session/session.db`、`contact/contact.db` 是三个核心。这个工具：

1. 扫描所有 `Weixin.exe` 进程的内存，定位 `Config.Cipher` 全局对象，直接读出每个数据库的 `(salt, enc_key)` —— 微信自己的内存里就有，不用 passphrase
2. 用 Cipher 解密到临时目录，只读副本，**原始库文件不会被改动**
3. 2 秒轮询 `SessionTable.last_timestamp`，配合 unread_count / real_sender_id / last_msg_locald_id 判定是否有新消息
4. 进程内 `(username, last_timestamp)` 去重 + 进程外 AppInstance 单实例，不会重复推

## 代码

仓库：https://github.com/TerminatorForMHT/wechat-message-assistant-windows

欢迎提 Issue / PR，尤其是数据库 schema 变化的反馈。

## v2.2.7 更新日志

- 📐 图标内容铺满画布（去掉人为透明留白），任务栏/开始菜单里与其他系统图标大小一致

## v2.2.6 更新日志

- 🔍 图标整体放大约 1.23 倍，减小透明边距

## v2.2.5 更新日志

- 🖼️ 同时提供 `altform-unplated` 与 `altform-lightunplated` 两套共 18 档尺寸资源，修复深色/浅色主题下图标出现底板边框的问题

## v2.2.4 更新日志

- 🗂️ 补齐 `Square44x44Logo` 各 targetsize 的 unplated 资源，避免系统回退到 plated 资源而加底板

## v2.2.3 更新日志

- 🌙 托盘菜单跟随系统深浅色（`SetPreferredAppMode` / `AllowDarkModeForWindow`），切换主题后重新打开菜单即生效

## v2.2.2 更新日志

- 🖱️ 托盘图标与右键菜单改为原生 `Shell_NotifyIcon` + `TrackPopupMenu`，彻底解决 H.NotifyIcon 两种菜单模式的问题（SecondWindow 文字截断、PopupMenu 点击无响应，退出无效）
- 🔁 监听资源管理器重启（`TaskbarCreated`）自动恢复托盘图标
- 🗑️ 移除 H.NotifyIcon 依赖

## v2.2.1 更新日志

- ✨ Toast 通知新增 **联系人圆形头像**（本地缓存 + 后台并发下载）
- 🔧 修复 Toast 中头像不显示的问题（MSIX 环境下必须用 `ms-appdata:///local/`，不能用 `file://`）
- 🚀 启动时刷新一次 username → 头像路径映射，推送路径只做字典查询
- 📦 头像缓存目录迁到 LocalState 以匹配 `ms-appdata:///local/`
- 代码路径、ToastService、AvatarService、MessageMonitor 都加了相应的日志输出

## v2.1.1 更新日志

- 🔐 **单实例保护**（AppInstance），解决双进程导致的同一条消息推两遍
- 🎯 **自己发送的消息不再推送**（用 `Name2Id.user_name` 动态解析 real_sender_id，不再写死）
- 📅 **历史同步消息门限**：只推送监听启动之后到达的消息
- 🛑 **过滤聚合会话** `brandsessionholder` / `brandservicesessionholder`，不推公众号文章、不推服务号同消息双份
- 🔕 免打扰会话在 `contact.chat_room_notify` 列自动识别
- 🎨 托盘菜单从 SecondWindow 改为 PopupMenu，修复菜单文字截断 / 渲染异常
- 🌟 AppIcon.ico 重制，去掉深色底板和边框
