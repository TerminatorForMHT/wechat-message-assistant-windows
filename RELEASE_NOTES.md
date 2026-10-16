# WxToastNotifier v2.0.0 发布说明

微信消息助手（WxToastNotifier）：非侵入式地把微信中「非免打扰」会话的新消息，转发到 Windows 通知中心（Toast），无需修改微信客户端、无需 HOOK 或注入。

## 本次更新

### 新增功能
- **最小化到托盘**：点击窗口右上角关闭按钮隐藏到系统托盘（不退出进程）；左键/双击托盘图标恢复主窗口，右键弹出 WinUI3 原生菜单（显示主窗口 / 退出）。
- **微信状态检测与一键拉起**：启动时自动检测微信是否运行，未运行时主界面显示「打开微信」按钮，可一键拉起已安装的微信。
- **多账户自动识别**：设备登录多个微信账户时，自动监控「当前登录」的账户（以数据库目录最新写入时间判定），无需手动切换。
- **当前账户显示微信号**：界面「当前账户」优先显示微信号（`contact.alias`），无微信号时回退为昵称，便于区分多账户。

### 问题修复
- **单聊消息免打扰过滤**：此前免打扰仅对群聊（`chat_room_notify` 列）生效，单聊恒为 0。现改为同时读取 `contact.flag` 第 9 位（`0x200`），单聊开免打扰后不再推送。
- **免打扰切换立即生效**：免打扰标记改为每轮轮询前刷新，切换免打扰后不再有最长 2 分钟的缓存延迟。
- **移除启动自动监听临时代码**：应用启动后不再自动开始监听，改由用户手动点击「开始监听」。

### 其他
- 实装微信图标（`src/img/WeChat.ico`），覆盖桌面图标、托盘图标与打包各尺寸资源。
- 项目已彻底移除全部 Python 实现，仅保留 C# + WinUI3 实现。

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
- 本机已登录微信（4.1+），数据库密钥在启动监听时自动从微信进程内提取（基于 WCDB Config.Cipher 方案，无需注入）。