# WxToastNotifier v2.0.0 发布说明

微信消息助手（WxToastNotifier）：非侵入式地把微信中「非免打扰」会话的新消息转发到 Windows 通知中心（Toast）。本版本是对原 Python 版的 **C# + WinUI3 完全重写**，核心能力与 Python 版保持一致，并修复了单聊免打扰、新增托盘等改进。

## 核心功能（继承自 Python 版）

- **密钥自动提取**：从微信进程内存扫描定位 WCDB Config.Cipher 配置对象，解码并 HMAC 校验得到数据库密钥，无需注入微信、不修改任何微信文件，提取到的密钥全部通过 HMAC 交叉验证。
- **数据库解密**：按会话/联系人/消息库分别解密到临时目录，绝不改动微信原始数据。
- **新消息监控与 Toast 推送**：轮询消息库检测新消息，转发到 Windows 通知中心（AppNotification 原生 Toast）。
- **消息免打扰过滤**：群聊按 `contact.chat_room_notify`、单聊按 `contact.flag` 第 9 位（`0x200`）判定，被静音的会话不推送。
- **过滤自己发出的消息**：按 `real_sender_id == 2` 识别自己发出的消息，避免推送自己发送的内容。
- **多账户识别**：设备登录多个微信账户时，自动选择写入时间最新的数据库作为当前账户。

## 相对 Python 版的变化

- **完全重写为 C# + WinUI3**：不再依赖 Python 运行时，打包为单个 MSIX，安装与分发更简单。
- **最小化到托盘**：关闭窗口隐藏到系统托盘（不退出进程）；托盘右键菜单为 WinUI3 原生样式。
- **微信状态检测与一键拉起**：启动时检测微信是否运行，未运行时显示「打开微信」按钮一键拉起。
- **当前账户显示微信号**：优先显示 `contact.alias`（微信号），无微信号回退昵称，便于区分多账户。
- **单聊免打扰修复**：单聊免打扰此前未生效（字段仅对群聊存在），现改读 `contact.flag` 第 9 位。
- **免打扰切换立即生效**：免打扰标记每轮轮询前刷新，不再有缓存延迟。

## 安装说明

本版本使用**自签名证书**打包为 MSIX，首次安装前需先信任证书：

1. 双击 `WxToastNotifier.cer` 安装证书，选择「本地计算机」→「受信任的根证书颁发机构」。
   - 或管理员 PowerShell 导入：
     ```powershell
     Import-Certificate -FilePath .\WxToastNotifier.cer -CertStoreLocation Cert:\LocalMachine\Root
     ```
2. 双击 `WxToastNotifier_2.0.0.0_x64.msix` 安装，或：
   ```powershell
   Add-AppxPackage -Path .\WxToastNotifier_2.0.0.0_x64.msix
   ```

> 证书为本地自签名，仅用于侧载（sideload）分发；如需商店发布，请改用受信任的代码签名证书。

## 产物

- 安装包：`WxToastNotifier/AppPackages/WxToastNotifier_2.0.0.0_x64_Test/WxToastNotifier_2.0.0.0_x64.msix`
- 签名证书（公钥）：`WxToastNotifier/WxToastNotifier.cer`
- 签名证书（私钥，仅构建机保留，勿分发）：`WxToastNotifier/WxToastNotifier.pfx`

## 使用前提

- Windows 10 1809（17763）及以上。
- 本机已登录微信（4.1+），数据库密钥在启动监听时自动提取。